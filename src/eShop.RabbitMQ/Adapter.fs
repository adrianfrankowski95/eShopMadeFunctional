namespace eShop.RabbitMQ

open System
open System.Collections.Immutable
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

type internal RetryCount = int

type RabbitMQEventDispatcher<'eventPayload> = Event<'eventPayload> -> TaskResult<unit, RabbitMQIoError>

type private EventHandler<'payload, 'err> = Event<'payload> -> TaskResult<unit, 'err>

type internal EventType =
    | Object of Type
    | Union of UnionCaseInfo * Type

type internal RabbitMQ = RabbitMQ

type private EventBody = byte array

type RabbitMQSubscriptionError =
    | InvalidEventData of string
    | DeserializationError of exn
    | UnhandledEvent of EventName * EventBody
    | HandlerError of obj

type RabbitMQPublishError =
    | SerializationError of exn
    | ChannelError of exn

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

    type State =
        internal
            { Publisher: IChannel
              Consumer: AsyncEventingBasicConsumer }

    let private createConnectionFactory connectionString : unit -> ValueTask<IConnection> =
        let mutable connection: IConnection option = None

        let factory =
            ConnectionFactory(
                Uri = Uri(connectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(3.0),
                RequestedHeartbeat = TimeSpan.FromSeconds(3.0),
                TopologyRecoveryEnabled = true
            )

        fun () ->
            task {
                let! conn =
                    connection
                    |> Option.map (fun conn ->
                        if conn.IsOpen then
                            ValueTask<IConnection>(conn)
                        else
                            task {
                                do! conn.DisposeAsync()
                                return! factory.CreateConnectionAsync()
                            }
                            |> ValueTask<IConnection>)
                    |> Option.defaultWith (fun () -> factory.CreateConnectionAsync() |> ValueTask<IConnection>)

                connection <- Some conn
                return conn
            }
            |> ValueTask<IConnection>


    let private configureDeadLetters (options: RabbitMQOptions) (channel: IChannel) =
        taskResult {
            let queueName = options.SubscriptionClientName

            do!
                channel.ExchangeDeclareAsync(
                    exchange = DeadLetterExchangeName,
                    ``type`` = "direct",
                    durable = true,
                    autoDelete = false
                )

            do!
                channel.QueueDeclareAsync(
                    queue = DeadLetterQueueName,
                    durable = true,
                    exclusive = false,
                    autoDelete = false,
                    arguments = null
                )
                |> Task.ignore

            do!
                channel.QueueBindAsync(
                    queue = DeadLetterQueueName,
                    exchange = DeadLetterExchangeName,
                    routingKey = queueName
                )
        }

    let private configureConsumerExchange (options: RabbitMQOptions) (channel: IChannel) =
        taskResult {
            let queueName = options.SubscriptionClientName

            do! channel.ExchangeDeclareAsync(exchange = ExchangeName, ``type`` = "direct")

            let arguments: Map<string, obj> =
                Map.empty
                |> Map.add "x-dead-letter-exchange" DeadLetterExchangeName
                |> Map.add "x-dead-letter-routing-key" queueName
                |> Map.mapValues box

            do!
                channel.QueueDeclareAsync(
                    queue = queueName,
                    durable = true,
                    exclusive = false,
                    autoDelete = false,
                    arguments = arguments
                )
                |> Task.ignore

            do! channel.BasicQosAsync(prefetchSize = 0u, prefetchCount = 10us, ``global`` = false)

            let consumer = AsyncEventingBasicConsumer(channel)

            do!
                channel.BasicConsumeAsync(queue = queueName, autoAck = false, consumer = consumer)
                |> Task.ignore

            return consumer
        }

    let private configureConsuming (connection: IConnection) options =
        taskResult {
            let! channel = connection.CreateChannelAsync()

            do! channel |> configureDeadLetters options

            let! consumer = channel |> configureConsumerExchange options

            return consumer
        }

    let private configurePublishing (connection: IConnection) =
        taskResult {
            let! channel =
                connection.CreateChannelAsync(
                    CreateChannelOptions(
                        publisherConfirmationsEnabled = true,
                        publisherConfirmationTrackingEnabled = true
                    )
                )

            do! channel.ExchangeDeclareAsync(ExchangeName, ``type`` = "direct")

            return channel
        }

    let internal createTopology connectionString options =
        taskResult {
            let createConnection = createConnectionFactory connectionString

            let! connection = createConnection ()

            let! publisher = configurePublishing connection
            let! consumer = configureConsuming connection options
            
            return
                { Publisher = publisher
                  Consumer = consumer }
        }

    let internal subscribe
        (options: RabbitMQOptions)
        (jsonOptions: JsonSerializerOptions)
        (getUtcNow: GetUtcNow)
        (logger: ILogger<RabbitMQ>)
        (retry: BasicDeliverEventArgs -> RetryCount -> TaskResult<unit, RabbitMQPublishError>)
        (eventHandlers: Map<EventName, EventType * EventHandler<_, _>>)
        (state: State)
        =
        taskResult {
            let queueName = options.SubscriptionClientName
            let maxRetryCount = options.RetryCount

            do!
                eventHandlers
                |> Map.keys
                |> Task.createColdSeq (fun eventName ->
                    state.Consumer.Channel.QueueBindAsync(
                        queue = queueName,
                        exchange = ExchangeName,
                        routingKey = EventName.value eventName
                    )
                    |> Task.ofUnit)
                |> Task.sequential
                |> Task.ignore

            state.Consumer.add_ReceivedAsync (fun _ ea ->
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
                                let! payload =
                                    bodyBytes
                                    |> Json.deserializeBody jsonOptions eventType
                                    |> Result.mapError DeserializationError

                                return!
                                    { Id = eventId
                                      OccurredAt = timestamp
                                      Data = payload }
                                    |> handler
                                    |> TaskResult.mapError HandlerError
                            })
                        |> Option.defaultWith (fun () -> (eventName, bodyBytes) |> UnhandledEvent |> TaskResult.error)
                }
                |> TaskResult.teeAsync (fun _ ->
                    task {
                        do! state.Consumer.Channel.BasicAckAsync(ea.DeliveryTag, multiple = false)

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

                        let headers =
                            ea.BasicProperties.Headers
                            |> Option.ofObj
                            |> Option.defaultValue ImmutableDictionary.Empty

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

                        do! state.Consumer.Channel.BasicNackAsync(ea.DeliveryTag, multiple = false, requeue = false)
                    })
                |> TaskResult.ignoreError
                :> Task)
        }

    let internal publish
        (jsonOptions: JsonSerializerOptions)
        (options: Configuration.RabbitMQOptions)
        (state: State)
        (eventName: EventName)
        (event: Event<obj>)
        =
        taskResult {
            let! body =
                event.Data
                |> Json.serializeBody jsonOptions
                |> Result.mapError SerializationError

            let rawEventName = EventName.value eventName

            let properties = BasicProperties()
            properties.MessageId <- event.Id |> EventId.toString
            properties.Type <- rawEventName
            properties.DeliveryMode <- DeliveryModes.Persistent
            properties.Timestamp <- event.OccurredAt.ToUnixTimeSeconds() |> AmqpTimestamp
            properties.ContentType <- "application/json"
            properties.Persistent <- true
            properties.Expiration <- options.MessageTtl.TotalMilliseconds |> int |> string

            do!
                TaskResult.catch ChannelError (fun () ->
                    state.Consumer.Channel
                        .BasicPublishAsync(
                            exchange = ExchangeName,
                            routingKey = rawEventName,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )
                        .AsTask()
                    |> Task.ofUnit)
        }

    let internal retry
        (state: State)
        (getUtcNow: GetUtcNow)
        (ea: BasicDeliverEventArgs)
        (retryCount: int)
        : TaskResult<unit, RabbitMQPublishError> =
        taskResult {
            let newRetryCount = retryCount + 1
            let retryTimestamp = getUtcNow () |> _.ToUnixTimeSeconds() |> AmqpTimestamp

            let properties = BasicProperties(ea.BasicProperties)

            let headers =
                properties.Headers
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    properties.Headers <- ImmutableDictionary.Empty
                    properties.Headers)

            headers.Add(RetryCountArgName, newRetryCount)
            headers.Add(RetryTimestampArgName, retryTimestamp)

            do!
                TaskResult.catch ChannelError (fun () ->
                    state.Publisher
                        .BasicPublishAsync(
                            exchange = ea.Exchange,
                            routingKey = ea.RoutingKey,
                            mandatory = true,
                            basicProperties = properties,
                            body = ea.Body
                        )
                        .AsTask()
                    |> Task.ofUnit)
        }
