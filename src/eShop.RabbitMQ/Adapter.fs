namespace eShop.RabbitMQ

open System
open System.Collections.Immutable
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open RabbitMQ.Client
open RabbitMQ.Client.Events
open FsToolkit.ErrorHandling
open eShop.RabbitMQ
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.Prelude
open eShop.RabbitMQ.Configuration

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

type Priority =
    | High
    | Regular

type RabbitMQIoError =

    | SerializationError of exn
    | ChannelCreationError of exn
    | ExchangeDeclarationError of exn
    | EventDispatchError of exn
    | InvalidEventData of string

type private RetryCount = int
type private EventBody = byte array

type RabbitMQSubscriptionIoError =
    | InvalidEventData of string
    | DeserializationError of exn
    | UnhandledEvent of EventName * EventBody
    | HandlerError of obj

type RabbitMQEventDispatcher<'eventPayload> = Event<'eventPayload> -> TaskResult<unit, RabbitMQIoError>

type private EventHandler<'payload, 'err> = Event<'payload> -> TaskResult<unit, 'err>

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
        |> Result.mapError DeserializationError

type private RabbitMQ = RabbitMQ

[<RequireQualifiedAccess>]
module internal RabbitMQ =
    [<Literal>]
    let internal ExchangeName = "eshop_event_bus"

    [<Literal>]
    let internal DeadLetterExchangeName = "eshop_event_bus_dlx"

    [<Literal>]
    let internal DeadLetterQueueName = "eshop_event_bus_dlq"

    [<Literal>]
    let internal RetryCountArgName = "x-retry-count"

    [<Literal>]
    let internal RetryTimestampArgName = "x-retry-timestamp"

    let private configureDeadLetters (options: RabbitMQOptions) (channel: IModel) =
        let queueName = options.SubscriptionClientName

        channel.ExchangeDeclare(
            exchange = DeadLetterExchangeName,
            ``type`` = "direct",
            durable = true,
            autoDelete = false
        )

        channel.QueueDeclare(
            queue = DeadLetterQueueName,
            durable = true,
            exclusive = false,
            autoDelete = false,
            arguments = null
        )
        |> ignore

        channel.QueueBind(queue = DeadLetterQueueName, exchange = DeadLetterExchangeName, routingKey = queueName)
        
        channel

    let private configureExchange (options: RabbitMQOptions) (channel: IModel) =
        let queueName = options.SubscriptionClientName

        channel.ExchangeDeclare(exchange = ExchangeName, ``type`` = "direct")

        let arguments: Map<string, obj> =
            Map.empty
            |> Map.add "x-dead-letter-exchange" DeadLetterExchangeName
            |> Map.add "x-dead-letter-routing-key" options.SubscriptionClientName
            |> Map.mapValues box

        channel.QueueDeclare(
            queue = queueName,
            durable = true,
            exclusive = false,
            autoDelete = false,
            arguments = arguments
        )
        |> ignore

        channel.BasicQos(prefetchSize = 0u, prefetchCount = 1us, ``global`` = false)

        let consumer = AsyncEventingBasicConsumer(channel)

        channel.BasicConsume(queue = queueName, autoAck = false, consumer = consumer)
        |> ignore

        consumer, channel

    let private subscribe
        (retry: BasicDeliverEventArgs -> RetryCount -> TaskResult<unit, RabbitMQIoError>)
        (options: RabbitMQOptions)
        (jsonOptions: JsonSerializerOptions)
        (getUtcNow: GetUtcNow)
        (logger: ILogger<RabbitMQ>)
        (eventHandlers: Map<EventName, EventType * EventHandler<_, _>>)
        (consumer: AsyncEventingBasicConsumer)
        =

        let queueName = options.SubscriptionClientName
        let maxRetryCount = options.RetryCount

        eventHandlers
        |> Map.keys
        |> Seq.iter (fun eventName ->
            consumer.Model.QueueBind(
                queue = queueName,
                exchange = ExchangeName,
                routingKey = EventName.value eventName
            ))

        consumer.add_Received (fun _ ea ->
            taskResult {
                let timestamp =
                    match ea.BasicProperties.IsTimestampPresent() with
                    | true -> ea.BasicProperties.Timestamp.UnixTime |> DateTimeOffset.FromUnixTimeSeconds
                    | false -> getUtcNow ()

                let! eventId =
                    ea.BasicProperties.MessageId
                    |> EventId.ofString
                    |> Result.mapError InvalidEventData

                let! eventName = ea.RoutingKey |> EventName.create |> Result.mapError InvalidEventData

                let bodyBytes = ea.Body.ToArray()

                do!
                    eventHandlers
                    |> Map.tryFind eventName
                    |> Option.map (fun (eventType, handler) ->
                        taskResult {
                            let! payload = bodyBytes |> Json.deserializeBody jsonOptions eventType

                            return!
                                { Id = eventId
                                  OccurredAt = timestamp
                                  Data = payload }
                                |> handler
                                |> TaskResult.mapError HandlerError
                        })
                    |> Option.defaultWith (fun () -> (eventName, bodyBytes) |> UnhandledEvent |> TaskResult.error)
            }
            |> TaskResult.tee (fun _ ->
                consumer.Model.BasicAck(ea.DeliveryTag, multiple = false)

                logger.LogInformation(
                    "Successfully acknowledged MessageId {MessageId} of type {MessageType}",
                    ea.BasicProperties.MessageId,
                    ea.BasicProperties.Type
                ))
            |> TaskResult.teeErrorAsync (fun err ->
                task {
                    logger.LogError(
                        "An error occurred for MessageId {MessageId} of type {MessageType}: {Error}",
                        ea.BasicProperties.MessageId,
                        ea.BasicProperties.Type,
                        err
                    )

                    let headers =
                        ea.BasicProperties.Headers
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            ea.BasicProperties.Headers <- ImmutableDictionary.Empty
                            ea.BasicProperties.Headers)

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
                            retry ea retryCount
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

                    consumer.Model.BasicNack(ea.DeliveryTag, multiple = false, requeue = false)
                })
            |> TaskResult.ignoreError
            :> Task)

    let internal createChannel (connection: IConnection) = Result.catch connection.CreateModel

    let internal ensureExchange exchangeName (channel: IModel) =
        Result.catch (fun () ->
            channel.ExchangeDeclare(exchange = exchangeName, ``type`` = "direct")
            channel)

    let internal ensureDeadLetterExchange (channel: IModel) =
        Result.catch (fun () ->
            channel.ExchangeDeclare(
                exchange = DeadLetterExchangeName,
                ``type`` = "direct",
                durable = true,
                autoDelete = false
            ))

    let internal ensureDeadLetterQueue (channel: IModel) =
        Result.catch (fun () ->
            channel.QueueDeclare(
                queue = DeadLetterQueueName,
                durable = true,
                exclusive = false,
                autoDelete = false,
                arguments = null
            )
            |> ignore)

    let internal ensureQueue (QueueName queueName) (channel: IModel) =
        let arguments: Map<string, obj> =
            Map.empty
            |> Map.add "x-dead-letter-exchange" DeadLetterExchangeName
            |> Map.add "x-dead-letter-routing-key" queueName
            |> Map.mapValues box

        Result.catch (fun () ->
            channel.QueueDeclare(
                queue = queueName,
                durable = true,
                exclusive = false,
                autoDelete = false,
                arguments = arguments
            )
            |> ignore)

    let internal bindDeadLetterQueue (QueueName queueName) (channel: IModel) =
        Result.catch (fun () ->
            channel.QueueBind(Configuration.DeadLetterQueueName, Configuration.DeadLetterExchangeName, queueName))

    let internal bindQueue (QueueName queueName) routingKey (channel: IModel) =
        Result.catch (fun () -> channel.QueueBind(queueName, Configuration.ExchangeName, routingKey))

    let internal publish (EventName eventName) body properties (channel: IModel) =
        Result.catch (fun () ->
            channel.BasicPublish(
                exchange = Configuration.ExchangeName,
                routingKey = eventName,
                mandatory = true,
                basicProperties = properties,
                body = body
            ))

    let internal createConsumer (QueueName queueName) (channel: IModel) =
        Result.catch (fun () ->
            channel.BasicQos(0u, 1us, false)

            let consumer = AsyncEventingBasicConsumer(channel)

            channel.BasicConsume(queue = queueName, autoAck = false, consumer = consumer)
            |> ignore

            consumer)

    let internal ack (ea: BasicDeliverEventArgs) (channel: IModel) =
        channel.BasicAck(ea.DeliveryTag, multiple = false)

    let internal nack (ea: BasicDeliverEventArgs) (channel: IModel) =
        channel.BasicNack(ea.DeliveryTag, multiple = false, requeue = false)

    let internal initConsumer (connection: IConnection) (config: Configuration.RabbitMQOptions) =
        taskResult {
            let ensureIsOpen (connection: IConnection) (retries: TimeSpan list) =
                let mutable isOpen = connection.IsOpen

                task {
                    for i in 0 .. retries.Length - 1 do
                        if not isOpen then
                            do! retries[i] |> Task.Delay
                            isOpen <- connection.IsOpen

                    return isOpen
                }


            let inline exnToMsg msg : Result<_, exn> -> Result<_, string> =
                Result.mapError (_.Message >> sprintf "%s: %s" msg)

            let! queueName = config.SubscriptionClientName |> QueueName.create

            do!
                [ (1: float); 2; 5 ]
                |> List.map TimeSpan.FromSeconds
                |> ensureIsOpen connection
                |> TaskResult.requireTrue "Connection to RabbitMQ was closed"

            let! channel = createChannel connection |> exnToMsg "Failed to create RabbitMQ channel"

            do!
                [ ensureDeadLetterExchange >> exnToMsg "Failed to declare RabbitMQ exchange"
                  ensureDeadLetterQueue >> exnToMsg "Failed to declare RabbitMQ queue"
                  bindDeadLetterQueue queueName >> exnToMsg "Failed to bind RabbitMQ queue"

                  ensureExchange >> exnToMsg "Failed to declare RabbitMQ exchange"
                  ensureQueue queueName >> exnToMsg "Failed to declare RabbitMQ queue" ]
                |> List.traverseResultA ((|>) channel)
                |> Result.ignore
                |> Result.mapError (String.concat Environment.NewLine)

            return!
                channel
                |> createConsumer queueName
                |> exnToMsg "Failed to create RabbitMQ consumer"
        }

    let internal registerEventHandler
        (eventNamesToHandle: EventName Set)
        (aggregateIdSelector: 'eventPayload -> AggregateId<'state>)
        (deserializePayload: EventName -> string -> Result<'eventPayload, exn>)
        (consumer: AsyncEventingBasicConsumer)
        (config: Configuration.RabbitMQOptions)
        (logger: ILogger<'eventPayload>)
        (getUtcNow: GetUtcNow)
        (persistEvents: PersistEvents<'state, 'eventPayload, 'ioError>)
        =
        result {
            let inline handleResult (ea: BasicDeliverEventArgs) =
                TaskResult.tee (fun _ ->
                    ack ea consumer.Model

                    logger.LogInformation(
                        "Successfully acknowledged MessageId {MessageId} of type {MessageType}",
                        ea.BasicProperties.MessageId,
                        ea.BasicProperties.Type
                    ))
                >> TaskResult.teeError (fun err ->
                    logger.LogError(
                        "An error occurred for MessageId {MessageId} of type {MessageType}: {Error}",
                        ea.BasicProperties.MessageId,
                        ea.BasicProperties.Type,
                        err
                    )

                    nack ea consumer.Model)
                >> TaskResult.ignoreError

            do!
                consumer.IsRunning
                |> Result.requireTrue $"""Consumer %s{consumer.ConsumerTags |> String.concat ", "} is not running"""

            let! queueName = config.SubscriptionClientName |> QueueName.create

            do!
                eventNamesToHandle
                |> Set.toList
                |> List.traverseResultA (fun (EventName eventName) -> consumer.Model |> bindQueue queueName eventName)
                |> Result.mapError (
                    List.map _.Message
                    >> String.concat Environment.NewLine
                    >> (+) "Failed to bind RabbitMQ queues: "
                )
                |> Result.ignore

            consumer.add_Received (fun _ ea ->
                taskResult {
                    let timestamp =
                        match ea.BasicProperties.IsTimestampPresent() with
                        | true -> ea.BasicProperties.Timestamp.UnixTime |> DateTimeOffset.FromUnixTimeSeconds
                        | false -> getUtcNow ()

                    let! eventId =
                        ea.BasicProperties.MessageId
                        |> EventId.ofString
                        |> Result.mapError (InvalidEventData >> Choice1Of2)

                    let! eventName =
                        ea.RoutingKey
                        |> EventName.create
                        |> Result.mapError (InvalidEventData >> Choice1Of2)

                    let! eventPayload =
                        Encoding.UTF8.GetString(ea.Body.Span)
                        |> deserializePayload eventName
                        |> Result.mapError (DeserializationError >> Choice1Of2)

                    let aggregateId = eventPayload |> aggregateIdSelector

                    do!
                        { Id = eventId
                          Data = eventPayload
                          OccurredAt = timestamp }
                        |> List.singleton
                        |> persistEvents aggregateId
                        |> TaskResult.mapError Choice2Of2
                }
                |> handleResult ea
                :> Task)
        }

    let createEventDispatcher
        (connection: IConnection)
        (getEventName: 'eventPayload -> Result<EventName, string>)
        (serializePayload: 'eventPayload -> Result<byte array, exn>)
        : RabbitMQEventDispatcher<'eventPayload> =
        fun (event: Event<'eventPayload>) ->
            taskResult {
                let! eventName = event.Data |> getEventName |> Result.mapError InvalidEventData

                let! body =
                    event.Data
                    |> serializePayload
                    |> Result.map ReadOnlyMemory
                    |> Result.mapError SerializationError

                use! channel = createChannel connection |> Result.mapError ChannelCreationError

                do!
                    channel
                    |> ensureExchange Configuration.ExchangeName
                    |> Result.mapError ExchangeDeclarationError

                let properties = channel.CreateBasicProperties()
                properties.MessageId <- event.Id |> EventId.toString
                properties.Type <- event |> Event.getType
                properties.DeliveryMode <- 2uy
                properties.Timestamp <- event.OccurredAt.ToUnixTimeSeconds() |> AmqpTimestamp
                properties.ContentType <- "application/json"
                properties.Persistent <- true

                return!
                    channel
                    |> publish eventName body properties
                    |> Result.mapError EventDispatchError
            }
