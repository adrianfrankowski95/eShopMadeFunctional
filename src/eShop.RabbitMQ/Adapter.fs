namespace eShop.RabbitMQ

open System
open System.Collections.Generic
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

type private RawBody = string

type EventHandlingError<'err> =
    | InvalidEventData of string
    | DeserializationError of exn
    | UnhandledEvent of EventName * RawBody
    | HandlerError of 'err

type EventHandler<'payload, 'err> = Event<'payload> -> TaskResult<unit, 'err>

type private BoxedEventHandler = Event<obj> -> TaskResult<unit, obj>

type EventHandlers = private EventHandlers of Dictionary<EventName, EventType * BoxedEventHandler>

[<RequireQualifiedAccess>]
module EventHandlers =
    let private getEventTypes<'payload> =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true ->
            FSharpType.GetUnionCases(payloadType, false)
            |> Array.map (fun unionCase ->
                let eventName = unionCase.Name |> EventName.create |> Result.valueOr failwith
                let caseDataType = unionCase.GetFields() |> Array.head |> _.PropertyType

                eventName, EventType.Union(unionCase, caseDataType))
        | false ->
            let eventName = payloadType.Name |> EventName.create |> Result.valueOr failwith

            [| eventName, EventType.Object payloadType |]

    let empty () = Dictionary() |> EventHandlers

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
        |> Option.defaultWith (fun () ->
            (eventName, event.Data |> System.Text.Encoding.UTF8.GetString)
            |> UnhandledEvent
            |> TaskResult.error)

type PublishError =
    | SerializationError of exn
    | ChannelError of exn

type Publisher = private Publisher of IChannel

[<RequireQualifiedAccess>]
module Publisher =
    let internal getEventName (ev: Event<'payload>) =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true ->
            FSharpValue.GetUnionFields(ev.Data, payloadType)
            |> function
                | unionCase, [||] -> unionCase.Name
                | _, caseData -> caseData |> Array.head |> _.GetType().Name
        | false -> payloadType.Name
        |> EventName.create
        |> Result.valueOr failwith

    let internal create (connection: IConnection) =
        task {
            let! channel =
                connection.CreateChannelAsync(
                    CreateChannelOptions(
                        publisherConfirmationsEnabled = true,
                        publisherConfirmationTrackingEnabled = true
                    )
                )

            channel.add_BasicReturnAsync (fun _ ea ->
                task {
                    let body = ea.Body.ToArray()

                    let properties = BasicProperties(ea.BasicProperties)

                    let headers =
                        properties.Headers
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            properties.Headers <- Dictionary()
                            properties.Headers)

                    headers.Add(UnroutableArgName, true)
                    headers.Add(OriginalExchangeArgName, ea.Exchange)

                    do!
                        channel.BasicPublishAsync(
                            exchange = MainDeadLetterExchangeName,
                            mandatory = true,
                            routingKey = ea.RoutingKey,
                            basicProperties = properties,
                            body = body
                        )
                })

            return channel |> Publisher
        }

    let internal internalPublish
        (jsonOptions: JsonSerializerOptions)
        (Publisher publisher)
        (EventName eventName)
        (event: Event<obj>)
        =
        taskResult {
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
                TaskResult.catch ChannelError (fun () ->
                    publisher
                        .BasicPublishAsync(
                            exchange = MainExchangeName,
                            routingKey = eventName,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )
                        .AsTask()
                    |> Task.ofUnit)
        }

    let inline publish (jsonOptions: JsonSerializerOptions) (publisher: Publisher) (event: Event<'payload>) =
        let eventName = event |> getEventName
        let body = event |> Event.mapPayload box

        internalPublish jsonOptions publisher eventName body

type internal ConsumerHandler<'err> = EventName -> Event<byte[]> -> TaskResult<unit, 'err>

type Consumer = private Consumer of AsyncEventingBasicConsumer

[<RequireQualifiedAccess>]
module Consumer =
    let internal create (connection: IConnection) (options: EventBusOptions) =
        task {
            let client = options.SubscriptionClientName

            let! channel = connection.CreateChannelAsync()

            do! channel.BasicQosAsync(prefetchSize = 0u, prefetchCount = 10us, ``global`` = false)

            let consumer = channel |> AsyncEventingBasicConsumer

            do!
                channel.BasicConsumeAsync(queue = client, autoAck = false, consumer = consumer)
                |> Task.ignore

            return consumer |> Consumer
        }

    let subscribe (logger: ILogger<RabbitMQ>) (handler: ConsumerHandler<'err>) (Consumer consumer) =
        consumer.add_ReceivedAsync (fun _ ea ->
            taskResult {
                let! timestamp =
                    match ea.BasicProperties.IsTimestampPresent() with
                    | true ->
                        ea.BasicProperties.Timestamp.UnixTime
                        |> DateTimeOffset.FromUnixTimeSeconds
                        |> Ok
                    | false -> "Missing Timestamp property" |> InvalidEventData |> Error

                let! eventId =
                    ea.BasicProperties.MessageId
                    |> EventId.ofString
                    |> Result.mapError InvalidEventData

                let! eventName = ea.RoutingKey |> EventName.create |> Result.mapError InvalidEventData

                let event =
                    { Id = eventId
                      OccurredAt = timestamp
                      Data = ea.Body.ToArray() }

                do! event |> handler eventName |> TaskResult.mapError HandlerError
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
                    logger.LogError(
                        "An error occurred for MessageId {MessageId} of type {MessageType}: {Error}",
                        ea.BasicProperties.MessageId,
                        ea.BasicProperties.Type,
                        err
                    )

                    do! consumer.Channel.BasicNackAsync(ea.DeliveryTag, multiple = false, requeue = false)
                })
            |> TaskResult.ignoreError
            :> Task)

type EventBus = private EventBus of IConnection * EventBusOptions

[<RequireQualifiedAccess>]
module EventBus =
    let private configureMainExchange (topologyChannel: IChannel) =
        task {
            do!
                topologyChannel.ExchangeDeclareAsync(
                    exchange = MainDeadLetterExchangeName,
                    ``type`` = "fanout",
                    durable = true,
                    autoDelete = false
                )

            do!
                topologyChannel.QueueDeclareAsync(
                    queue = MainDeadLetterQueueName,
                    durable = true,
                    exclusive = false,
                    autoDelete = false,
                    arguments = null
                )
                |> Task.ignore

            do!
                topologyChannel.QueueBindAsync(
                    queue = MainDeadLetterQueueName,
                    exchange = MainDeadLetterExchangeName,
                    routingKey = MainDeadLetterQueueName
                )

            do! topologyChannel.ExchangeDeclareAsync(exchange = MainExchangeName, ``type`` = "direct")

            topologyChannel.add_BasicReturnAsync (fun _ ea ->
                task {
                    let body = ea.Body.ToArray()

                    let properties = BasicProperties(ea.BasicProperties)

                    let headers =
                        properties.Headers
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            properties.Headers <- Dictionary()
                            properties.Headers)

                    headers.Add(UnroutableArgName, true)
                    headers.Add(OriginalExchangeArgName, ea.Exchange)

                    do!
                        topologyChannel.BasicPublishAsync(
                            exchange = MainDeadLetterExchangeName,
                            mandatory = true,
                            routingKey = ea.RoutingKey,
                            basicProperties = properties,
                            body = body
                        )
                })
        }

    let private configureClient
        (options: EventBusOptions)
        (eventHandlers: EventHandlers)
        (Publisher publisher)
        (topologyChannel: IChannel)
        =
        task {
            let client = options.SubscriptionClientName
            let messageTtl = options.MessageTtl.TotalMilliseconds |> int |> string

            let retryRouter = $"%s{client}_retry_router"
            let createRetryName i = $"%s{client}_retry_%d{i}"

            // Main client exchange
            do!
                topologyChannel.ExchangeDeclareAsync(
                    exchange = client,
                    ``type`` = "direct",
                    durable = true,
                    autoDelete = false
                )

            // Exchanges and Queues that republish messages after a delay
            do!
                options.Retries
                |> Task.createColdSeqi (fun i retry ->
                    task {
                        let delay = retry.TotalMilliseconds |> int |> string
                        let retryName = createRetryName (i + 1)

                        do!
                            topologyChannel.ExchangeDeclareAsync(
                                exchange = retryName,
                                ``type`` = "fanout",
                                durable = true,
                                autoDelete = false
                            )

                        // After the message is expired, it is automatically republished to the client exchange
                        let args: Map<string, obj> =
                            Map.empty
                            |> Map.add "x-dead-letter-exchange" client
                            |> Map.add "x-message-ttl" delay
                            |> Map.mapValues box

                        do!
                            topologyChannel.QueueDeclareAsync(
                                queue = retryName,
                                durable = true,
                                exclusive = false,
                                autoDelete = false,
                                arguments = args
                            )
                            |> Task.ignore

                        do!
                            topologyChannel.QueueBindAsync(
                                queue = retryName,
                                exchange = retryName,
                                routingKey = retryName
                            )
                    })
                |> Task.sequential
                |> Task.ignore

            // Final retry exchange that will put messages to the global DLX after all retries
            let maxRetryCount = options.Retries |> Array.length
            let finalRetryName = createRetryName (maxRetryCount + 1)

            do!
                topologyChannel.ExchangeDeclareAsync(
                    exchange = finalRetryName,
                    ``type`` = "fanout",
                    durable = true,
                    autoDelete = false
                )

            do!
                topologyChannel.ExchangeBindAsync(
                    source = finalRetryName,
                    destination = MainDeadLetterExchangeName,
                    routingKey = finalRetryName
                )

            // Main client queue publishing to retry router on failure or expiration
            let clientQueueArgs: Map<string, obj> =
                Map.empty
                |> Map.add "x-dead-letter-exchange" retryRouter
                |> Map.add "x-message-ttl" messageTtl
                |> Map.mapValues box

            do!
                topologyChannel.QueueDeclareAsync(
                    queue = client,
                    durable = true,
                    exclusive = false,
                    autoDelete = false,
                    arguments = clientQueueArgs
                )
                |> Task.ignore

            // Bind all event names as routing keys to enable receiving them
            do!
                eventHandlers
                |> EventHandlers.getEventNames
                |> Task.createColdSeq (fun (EventName eventName) ->
                    task {
                        do!
                            topologyChannel.ExchangeBindAsync(
                                source = MainExchangeName,
                                destination = client,
                                routingKey = eventName
                            )

                        do!
                            topologyChannel.QueueBindAsync(queue = client, exchange = client, routingKey = eventName)
                            |> Task.ofUnit
                    })
                |> Task.sequential
                |> Task.ignore

            // Configure retry router, consumes failed messages from main queue and forwards to desired waiting queue
            do!
                topologyChannel.ExchangeDeclareAsync(
                    exchange = retryRouter,
                    ``type`` = "fanout",
                    durable = true,
                    autoDelete = false
                )

            let args: Map<string, obj> =
                Map.empty
                |> Map.add "x-dead-letter-exchange" MainDeadLetterExchangeName
                |> Map.add "x-message-ttl" messageTtl
                |> Map.mapValues box

            do!
                topologyChannel.QueueDeclareAsync(
                    queue = retryRouter,
                    durable = true,
                    exclusive = false,
                    autoDelete = false,
                    arguments = args
                )
                |> Task.ignore

            do! topologyChannel.QueueBindAsync(queue = retryRouter, exchange = retryRouter, routingKey = retryRouter)

            let consumer = AsyncEventingBasicConsumer(topologyChannel)

            do!
                topologyChannel.BasicQosAsync(prefetchSize = 0u, prefetchCount = 10us, ``global`` = false)
                |> Task.ofUnit

            consumer.add_ReceivedAsync (fun _ ea ->
                task {
                    let body = ea.Body.ToArray()

                    let properties = BasicProperties(ea.BasicProperties)

                    let headers =
                        properties.Headers
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            properties.Headers <- Dictionary()
                            properties.Headers)

                    let retryCount =
                        headers.TryGetValue(RetryCountArgName)
                        |> Option.ofPair
                        |> Option.map unbox<int>
                        |> Option.defaultValue 0
                        |> (+) 1

                    headers.Add(RetryCountArgName, retryCount)

                    do!
                        publisher.BasicPublishAsync(
                            exchange = createRetryName retryCount,
                            routingKey = ea.RoutingKey,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )

                    do! publisher.BasicAckAsync(ea.DeliveryTag, multiple = false)
                })

            do!
                topologyChannel.BasicConsumeAsync(queue = retryRouter, autoAck = false, consumer = consumer)
                |> Task.ignore
        }

    let private initializeTopology (connection: IConnection) (options: EventBusOptions) (eventHandlers: EventHandlers) =
        task {
            let! topologyChannel = connection.CreateChannelAsync()

            let! publisher = connection |> Publisher.create

            do! topologyChannel |> configureMainExchange
            do! topologyChannel |> configureClient options eventHandlers publisher
        }

    let create (factory: ConnectionFactory) (options: EventBusOptions) (eventHandlers: EventHandlers) =
        task {
            let! connection = factory.CreateConnectionAsync()

            let init = initializeTopology connection options

            do! eventHandlers |> init

            connection.add_RecoverySucceededAsync (fun _ _ -> eventHandlers |> init :> Task)

            return (connection, options) |> EventBus
        }

    let dispose (EventBus(connection, _)) = connection.Dispose()
