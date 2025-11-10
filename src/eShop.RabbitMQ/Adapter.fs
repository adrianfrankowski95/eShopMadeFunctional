namespace eShop.RabbitMQ

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open RabbitMQ.Client
open RabbitMQ.Client.Events
open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.Prelude
open eShop.RabbitMQ.Configuration

type RabbitMQ = RabbitMQ

type EventName = private EventName of string

[<RequireQualifiedAccess>]
module EventName =
    let create = String.Constraints.nonWhiteSpace EventName (nameof EventName)

    let value (EventName eventName) = eventName

type QueueName = private QueueName of string

[<RequireQualifiedAccess>]
module QueueName =
    let create =
        [ String.Constraints.nonWhiteSpace
          String.Constraints.hasMaxUtf8Bytes 255
          String.Constraints.doesntStartWith "amqp." ]
        |> String.Constraints.evaluateM QueueName (nameof QueueName)

type EventPriority =
    | High
    | Regular

type internal RetryCount = int

type private EventType =
    | Object of Type
    | Union of UnionCaseInfo * Type

module private Json =
    let deserializeBody (jsonOptions: JsonSerializerOptions) (eventType: EventType) (body: byte array) =
        Result.catch (fun () ->
            match eventType with
            | Object objType -> JsonSerializer.Deserialize(body, objType, jsonOptions)
            | Union(targetUnionCase, objType) ->
                let unionData = JsonSerializer.Deserialize(body, objType, jsonOptions)
                FSharpValue.MakeUnion(targetUnionCase, [| unionData |]))

    let serializeBody (jsonOptions: JsonSerializerOptions) (body: obj) =
        Result.catch (fun () -> JsonSerializer.SerializeToUtf8Bytes(body, jsonOptions) |> ReadOnlyMemory)

[<RequireQualifiedAccess>]
module ConnectionFactory =
    let create connectionString =
        ConnectionFactory(
            Uri = Uri(connectionString),
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(3.0),
            RequestedHeartbeat = TimeSpan.FromSeconds(3.0),
            TopologyRecoveryEnabled = true
        )

type private BoxedEventHandler = Event<obj> -> TaskResult<unit, obj>

type EventHandler<'payload, 'err> = Event<'payload> -> TaskResult<unit, 'err>

type private RawBody = string

type EventHandlingError =
    | InvalidEventData of string
    | DeserializationError of exn
    | UnhandledEvent of EventName * RawBody
    | HandlerError of obj

type EventHandlers = private EventHandlers of Dictionary<EventName, EventType * BoxedEventHandler>

[<RequireQualifiedAccess>]
module EventHandlers =
    let private getEventTypes<'payload> =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true ->
            FSharpType.GetUnionCases(payloadType, false)
            |> Array.map (fun targetCase ->
                let eventName = targetCase.Name |> EventName.create |> Result.valueOr failwith
                let targetCaseDataType = targetCase.GetFields() |> Array.head |> _.PropertyType

                eventName, EventType.Union(targetCase, targetCaseDataType))
        | false ->
            let eventName = payloadType.Name |> EventName.create |> Result.valueOr failwith

            [| eventName, EventType.Object payloadType |]

    let empty = Dictionary() |> EventHandlers

    let inline add (handler: EventHandler<'payload, 'err>) (EventHandlers handlers) =
        let eventTypes = getEventTypes<'payload>

        let boxedHandler (ev: Event<obj>) : TaskResult<unit, obj> =
            ev |> Event.mapPayload unbox<'payload> |> handler |> TaskResult.mapError box

        eventTypes
        |> Array.iter (fun (eventName, eventType) -> handlers.Add(eventName, (eventType, boxedHandler)))
        
        handlers |> EventHandlers

    let internal getEventNames (EventHandlers handlers) = handlers.Keys |> Seq.toList
    
    let internal invoke
        (jsonOptions: JsonSerializerOptions)
        (EventHandlers handlers)
        (eventName: EventName)
        (event: Event<byte[]>)
        =
        eventName
        |> handlers.TryGetValue
        |> Option.ofPair
        |> Option.map (fun (eventType, handler) ->
            event.Data
            |> Json.deserializeBody jsonOptions eventType
            |> Result.map (fun body -> event |> Event.mapPayload (fun _ -> body))
            |> Result.mapError DeserializationError
            |> TaskResult.ofResult
            |> TaskResult.bind (handler >> TaskResult.mapError HandlerError))
        |> Option.defaultWith (fun () -> (eventName, event.Data |> System.Text.Encoding.UTF8.GetString) |> UnhandledEvent |> TaskResult.error)

type TopologyCreationError =
    | DeadletterExchangeDeclarationError of exn
    | DeadletterQueueDeclarationError of exn
    | DeadletterBindingError of exn
    | ExchangeDeclarationError of exn
    | QueueDeclarationError of exn
    | HandlerBindingError of exn

type Topology = private Topology of IConnection

[<RequireQualifiedAccess>]
module Topology =
    let private configureDeadletters (channel: IChannel) (options: RabbitMQOptions) =
        taskResult {
            let queueName = options.SubscriptionClientName

            do!
                TaskResult.catch DeadletterExchangeDeclarationError (fun () ->
                    channel.ExchangeDeclareAsync(
                        exchange = DeadLetterExchangeName,
                        ``type`` = "direct",
                        durable = true,
                        autoDelete = false
                    )
                    |> Task.ofUnit)

            do!
                TaskResult.catch DeadletterQueueDeclarationError (fun () ->
                    channel.QueueDeclareAsync(
                        queue = DeadLetterQueueName,
                        durable = true,
                        exclusive = false,
                        autoDelete = false,
                        arguments = null
                    )
                    |> Task.ignore)

            do!
                TaskResult.catch DeadletterBindingError (fun () ->
                    channel.QueueBindAsync(
                        queue = DeadLetterQueueName,
                        exchange = DeadLetterExchangeName,
                        routingKey = queueName
                    )
                    |> Task.ofUnit)
        }

    let private declareExchangeAndQueue (channel: IChannel) (options: RabbitMQOptions) =
        taskResult {
            let queueName = options.SubscriptionClientName
            let messageTtl = options.MessageTtl.TotalMilliseconds |> int |> string

            do! TaskResult.catch ExchangeDeclarationError (fun () ->
                channel.ExchangeDeclareAsync(exchange = ExchangeName, ``type`` = "direct")
                |> Task.ofUnit)

            let arguments: Map<string, obj> =
                Map.empty
                |> Map.add "x-dead-letter-exchange" DeadLetterExchangeName
                |> Map.add "x-dead-letter-routing-key" queueName
                |> Map.add "x-message-ttl" messageTtl
                |> Map.mapValues box

            do!
                TaskResult.catch QueueDeclarationError (fun () -> 
                    channel.QueueDeclareAsync(
                        queue = queueName,
                        durable = true,
                        exclusive = false,
                        autoDelete = false,
                        arguments = arguments
                    )
                    |> Task.ignore)
        }

    let private bindEventHandlers (channel: IChannel) (options: RabbitMQOptions) (eventHandlers: EventHandlers) =
        eventHandlers
        |> EventHandlers.getEventNames
        |> Task.createColdSeq (fun eventName ->
            TaskResult.catch HandlerBindingError (fun () ->
                channel.QueueBindAsync(
                    queue = options.SubscriptionClientName,
                    exchange = ExchangeName,
                    routingKey = EventName.value eventName
                )
                |> Task.ofUnit))
        |> TaskResult.sequentialM
        |> TaskResult.ignore
    
    let create (options: RabbitMQOptions) (eventHandlers: EventHandlers) (factory: ConnectionFactory) =
        taskResult {
            let! connection = factory.CreateConnectionAsync()

            use! channel = connection.CreateChannelAsync()

            do! options |> configureDeadletters channel
            do! options |> declareExchangeAndQueue channel
            do! eventHandlers |> bindEventHandlers channel options

            return connection |> Topology
        }

type PublishError =
    | SerializationError of exn
    | ChannelPublishingError of exn

type PublisherCreationError =
    | ChannelCreationError of exn
    | ExchangeDeclarationError of exn

type Publisher = private Publisher of IChannel

[<RequireQualifiedAccess>]
module Publisher =
    let private getEventName (ev: Event<'payload>) : string =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true -> FSharpValue.GetUnionFields(ev.Data, payloadType) |> fst |> _.Name
        | false -> payloadType.Name

    let create (Topology connection) =
        taskResult {
            let! channel =
                TaskResult.catch ChannelCreationError (fun () ->
                    connection.CreateChannelAsync(
                        CreateChannelOptions(
                            publisherConfirmationsEnabled = true,
                            publisherConfirmationTrackingEnabled = true
                        )
                    ))

            return channel |> Publisher
        }

    let inline publish (jsonOptions: JsonSerializerOptions) (Publisher publisher) (event: Event<'payload>) =
        taskResult {
            let eventName = event |> getEventName

            let! body =
                event.Data
                |> Json.serializeBody jsonOptions
                |> Result.mapError SerializationError

            let properties = BasicProperties()
            properties.MessageId <- event.Id |> EventId.toString
            properties.Type <- eventName
            properties.DeliveryMode <- DeliveryModes.Persistent
            properties.Timestamp <- event.OccurredAt.ToUnixTimeSeconds() |> AmqpTimestamp
            properties.ContentType <- "application/json"
            properties.Persistent <- true

            do!
                TaskResult.catch ChannelPublishingError (fun () ->
                    publisher
                        .BasicPublishAsync(
                            exchange = ExchangeName,
                            routingKey = eventName,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )
                        .AsTask()
                    |> Task.ofUnit)
        }

    let internal retry
        (Publisher publisher)
        (getUtcNow: GetUtcNow)
        (eventName: string)
        (properties: BasicProperties)
        (body: ReadOnlyMemory<byte>)
        : TaskResult<unit, PublishError> =
        taskResult {
            let retryTimestamp = getUtcNow () |> _.ToUnixTimeSeconds() |> AmqpTimestamp

            let newRetryCount =
                properties.Headers.TryGetValue(RetryCountArgName)
                |> Option.ofPair
                |> Option.map unbox<int>
                |> Option.defaultValue 0
                |> (+) 1
            
            properties.Headers.Add(RetryCountArgName, newRetryCount)
            properties.Headers.Add(RetryTimestampArgName, retryTimestamp)

            do!
                TaskResult.catch ChannelPublishingError (fun () ->
                    publisher
                        .BasicPublishAsync(
                            exchange = ExchangeName,
                            routingKey = eventName,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )
                        .AsTask()
                    |> Task.ofUnit)
        }

type ConsumerError =
    | ConsumerCreationError of exn

type Consumer = private Consumer of AsyncEventingBasicConsumer

[<RequireQualifiedAccess>]
module Consumer =
    let create (options: RabbitMQOptions) (Topology connection) =
        taskResult {
            let queueName = options.SubscriptionClientName

            let! channel = TaskResult.catch ChannelCreationError connection.CreateChannelAsync

            do!
                TaskResult.catch ChannelCreationError (fun () ->
                    channel.BasicQosAsync(prefetchSize = 0u, prefetchCount = 10us, ``global`` = false)
                    |> Task.ofUnit)

            let consumer = AsyncEventingBasicConsumer(channel)

            do!
                TaskResult.catch ChannelCreationError (fun () ->
                    channel.BasicConsumeAsync(queue = queueName, autoAck = false, consumer = consumer)
                    |> Task.ignore)

            return consumer |> Consumer
        }

    let subscribe
        (options: RabbitMQOptions)
        (getUtcNow: GetUtcNow)
        (logger: ILogger<RabbitMQ>)
        (retry: string -> BasicProperties -> byte[] -> TaskResult<unit, PublishError>)
        (handler: EventName -> Event<byte[]> -> TaskResult<unit, _>)
        (Consumer consumer)
        =
            let maxRetryCount = options.RetryCount
            
            consumer.add_ReceivedAsync (fun _ ea ->
                taskResult {
                    let! eventId =
                        ea.BasicProperties.MessageId
                        |> EventId.ofString
                        |> Result.mapError InvalidEventData

                    let! eventName = ea.RoutingKey |> EventName.create |> Result.mapError InvalidEventData

                    let timestamp =
                        match ea.BasicProperties.IsTimestampPresent() with
                        | true -> ea.BasicProperties.Timestamp.UnixTime |> DateTimeOffset.FromUnixTimeSeconds
                        | false -> getUtcNow ()

                    do!
                        { Id = eventId
                          OccurredAt = timestamp
                          Data = ea.Body.ToArray() }
                        |> handler eventName
                }
                |> TaskResult.teeAsync (fun _ ->
                    task {
                        do! consumer.Channel.BasicAckAsync(ea.DeliveryTag, multiple = false)

                        logger.LogInformation(
                            "Successfully acknowledged MessageId {MessageId} of type {MessageType}",
                            ea.BasicProperties.MessageId,
                            ea.BasicProperties.Type
                        )
                    })
                |> TaskResult.teeErrorAsync (fun err ->
                    task {
                        let errorMsg = err.ToString()

                        logger.LogError(
                            "An error occurred for MessageId {MessageId} of type {MessageType}: {Error}",
                            ea.BasicProperties.MessageId,
                            ea.BasicProperties.Type,
                            errorMsg
                        )

                        let properties = BasicProperties(ea.BasicProperties)
                        
                        let headers =
                            properties.Headers
                            |> Option.ofObj
                            |> Option.defaultWith (fun () ->
                                properties.Headers <- ImmutableDictionary.Empty
                                properties.Headers)
                        
                        headers.Add("x-dead-letter-reason", errorMsg)

                        let retryCount =
                            headers.TryGetValue(RetryCountArgName)
                            |> Option.ofPair
                            |> Option.map unbox<int>
                            |> Option.defaultValue 0

                        match retryCount < maxRetryCount with
                        | true ->
                            logger.LogWarning(
                                "Message with MessageId {MessageId} of type {MessageType} will be retried: [{Retry}/{MaxRetries}]",
                                ea.BasicProperties.MessageId,
                                ea.BasicProperties.Type,
                                retryCount,
                                maxRetryCount
                            )

                            do!
                                ea.Body.ToArray()
                                |> retry ea.RoutingKey properties
                                |> TaskResult.teeError (fun e ->
                                    logger.LogError(
                                        "Unexpected error when retrying Message with MessageId {MessageId} of type {MessageType}: {Error}",
                                        ea.BasicProperties.MessageId,
                                        ea.BasicProperties.Type,
                                        e
                                    ))
                                |> TaskResult.ignoreError
                        | false ->
                            logger.LogWarning(
                                "Message with MessageId {MessageId} of type {MessageType} reached max retry count: {Retry}",
                                ea.BasicProperties.MessageId,
                                ea.BasicProperties.Type,
                                retryCount
                            )

                        do! consumer.Channel.BasicNackAsync(ea.DeliveryTag, multiple = false, requeue = false)
                    })
                |> TaskResult.ignoreError
                :> Task)
