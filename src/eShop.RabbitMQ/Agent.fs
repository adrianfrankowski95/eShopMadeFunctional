[<RequireQualifiedAccess>]
module eShop.RabbitMQ

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
open eShop.DomainDrivenDesign
open eShop.Prelude

type private EventName = string
type private EventBody = byte array

type IoError =
    | DeserializationError of exn
    | SerializationError of exn
    | ChannelCreationError of exn
    | ExchangeDeclarationError of exn
    | EventDispatchError of exn
    | InvalidEventData of string
    | UnhandledEvent of EventName * EventBody

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

type private BoxedEventHandler = Event<obj> -> TaskResult<unit, obj>

type private RetryCount = int

type Priority = | High | Low

type private Message =
    | Publish of EventName * Event<obj> * Priority
    | Retry of BasicDeliverEventArgs * RetryCount

module private Message =
    let getHighPriority (msg: Message) =
        match msg with
        | Publish (_, _, priority) -> if priority = High then Some msg else None
        | Retry _-> None

type EventHandler<'payload, 'err> = Event<'payload> -> TaskResult<unit, 'err>

type State =
    private
        { GetOrCreateConnection: unit -> IConnection
          Channel: IModel option
          ConsumerChannel: IModel option
          Consumer: AsyncEventingBasicConsumer option
          EventHandlers: Map<EventName, EventType * BoxedEventHandler> }

[<RequireQualifiedAccess>]
module private State =
    let private getEventTypes<'payload> =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true ->
            FSharpType.GetUnionCases(payloadType, false)
            |> Array.map (fun targetCase ->
                let eventName = targetCase.Name
                let targetCaseDataType = targetCase.GetFields() |> Array.head |> _.PropertyType

                eventName, EventType.Union(targetCase, targetCaseDataType))
            |> List.ofArray
        | false -> [ payloadType.Name, EventType.Object payloadType ]

    let private createConnectionFactory connectionString =
        let mutable connection: IConnection option = None

        let factory =
            ConnectionFactory(
                Uri = Uri(connectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(3.0),
                RequestedHeartbeat = TimeSpan.FromSeconds(3.0),
                DispatchConsumersAsync = true
            )

        fun () ->
            let conn =
                connection
                |> Option.map (fun conn ->
                    if conn.IsOpen then
                        conn
                    else
                        conn.Dispose()
                        factory.CreateConnection())
                |> Option.defaultWith factory.CreateConnection

            connection <- Some conn
            conn

    let init connectionString =
        { GetOrCreateConnection = createConnectionFactory connectionString
          Channel = None
          ConsumerChannel = None
          Consumer = None
          EventHandlers = Map.empty }

    let withChannel channel state = { state with Channel = Some channel }

    let withConsumerChannel consumerChannel state =
        { state with
            ConsumerChannel = Some consumerChannel }

    let withConsumer consumer state = { state with Consumer = Some consumer }

    let addHandler (handler: EventHandler<'payload, 'err>) state =
        let eventTypes = getEventTypes<'payload>

        let boxedHandler (ev: Event<obj>) : TaskResult<unit, obj> =
            ev |> Event.mapPayload unbox<'payload> |> handler |> TaskResult.mapError box

        let newHandlers =
            eventTypes
            |> List.fold
                (fun handlers (eventName, eventType) -> handlers |> Map.add eventName (eventType, boxedHandler))
                state.EventHandlers

        { state with
            EventHandlers = newHandlers }

type Agent internal (initState: State, logger: ILogger<Agent>, getUtcNow: GetUtcNow, jsonOptions: JsonSerializerOptions, options: Configuration.RabbitMQOptions) =
    let getEventName (ev: Event<'payload>) : string =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true -> FSharpValue.GetUnionFields(ev.Data, payloadType) |> fst |> _.Name
        | false -> payloadType.Name

    let initTopology (inbox: MailboxProcessor<Message>)=
        let queueName = options.SubscriptionClientName
        let maxRetryCount = options.RetryCount

        let exchangeName = Configuration.ExchangeName
        let dlExchangeName = Configuration.DeadLetterExchangeName
        let dlQueueName = Configuration.DeadLetterQueueName

        let dlExchangeArgName = Configuration.DeadLetterExchangeArgName
        let dlRoutingKeyArgName = Configuration.DeadLetterRoutingKeyArgName

        let connection = initState.GetOrCreateConnection()
        
        let channel = connection.CreateModel()

        // Configure Dead Letter Exchange and Queue
        channel.ExchangeDeclare(
            exchange = dlExchangeName,
            ``type`` = "direct",
            durable = true,
            autoDelete = false
        )

        channel.QueueDeclare(
            queue = dlQueueName,
            durable = true,
            exclusive = false,
            autoDelete = false,
            arguments = null
        )
        |> ignore

        channel.QueueBind(queue = dlQueueName, exchange = dlExchangeName, routingKey = queueName)

        // Configure Main Exchange and Queue
        channel.ExchangeDeclare(exchange = exchangeName, ``type`` = "direct")

        let arguments: Map<string, obj> =
            Map.empty
            |> Map.add dlExchangeArgName (dlExchangeName |> box)
            |> Map.add dlRoutingKeyArgName (queueName |> box)

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

        initState.EventHandlers
        |> Map.keys
        |> Seq.iter (fun eventName ->
            consumer.Model.QueueBind(queue = queueName, exchange = exchangeName, routingKey = eventName))

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

                let eventName = ea.RoutingKey
                let bodyBytes = ea.Body.ToArray()

                do!
                    initState.EventHandlers
                    |> Map.tryFind eventName
                    |> Option.map (fun (eventType, handler) ->
                        taskResult {
                            let! payload =
                                bodyBytes
                                |> Json.deserializeBody jsonOptions eventType
                                |> Result.mapError Choice1Of2

                            return!
                                { Id = eventId
                                  OccurredAt = timestamp
                                  Data = payload }
                                |> handler
                                |> TaskResult.mapError Choice2Of2
                        })
                    |> Option.defaultWith (fun () ->
                        (eventName, bodyBytes) |> UnhandledEvent |> Choice1Of2 |> TaskResult.error)
            }
            |> TaskResult.tee (fun _ ->
                consumer.Model.BasicAck(ea.DeliveryTag, multiple = false)

                logger.LogInformation(
                    "Successfully acknowledged MessageId {MessageId} of type {MessageType}",
                    ea.BasicProperties.MessageId,
                    ea.BasicProperties.Type
                ))
            |> TaskResult.teeError (fun err ->
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
                    headers.TryGetValue(Configuration.RetryCountArgName)
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

                    (ea, retryCount) |> Retry |> inbox.Post
                | false ->
                    logger.LogWarning(
                        "Message with MessageId {MessageId} of type {MessageType} reached max retry count: {Retry}",
                        ea.BasicProperties.MessageId,
                        ea.BasicProperties.Type,
                        retryCount
                    )

                consumer.Model.BasicNack(ea.DeliveryTag, multiple = false, requeue = false))
            |> TaskResult.ignoreError
            :> Task)

        initState
        |> State.withConsumer consumer
        |> State.withConsumerChannel channel
    
    let processor = MailboxProcessor<Message>.Start(fun inbox ->
        let rec loop (state: State) =
            async {
                let! maybeHighPriority =
                    inbox.TryScan(Message.getHighPriority >> Option.map Async.singleton)
                
                let! msg =
                    maybeHighPriority
                    |> Option.map Async.singleton
                    |> Option.defaultWith inbox.Receive
                
                return! 
                    match msg with 
                    | Publish(eventName, event, _) ->
                        let exchangeName = Configuration.ExchangeName

                        let body =
                            JsonSerializer.SerializeToUtf8Bytes(event.Data, jsonOptions) |> ReadOnlyMemory

                        let channel =
                            state.Channel
                            |> Option.defaultWith (fun () ->
                                let connection = state.GetOrCreateConnection()
                                let channel = connection.CreateModel()
                                channel.ExchangeDeclare(exchange = exchangeName, ``type`` = "direct")
                                channel)

                        let properties = channel.CreateBasicProperties()
                        properties.MessageId <- event.Id |> EventId.toString
                        properties.Type <- eventName
                        properties.DeliveryMode <- 2uy
                        properties.Timestamp <- event.OccurredAt.ToUnixTimeSeconds() |> AmqpTimestamp
                        properties.ContentType <- "application/json"
                        properties.Persistent <- true
                        properties.Expiration <- options.MessageTtl.TotalMilliseconds |> int |> string

                        channel.BasicPublish(
                            exchange = exchangeName,
                            routingKey = eventName,
                            mandatory = true,
                            basicProperties = properties,
                            body = body
                        )

                        state |> State.withChannel channel

                    | Retry(ea, retryCount) ->
                        let retryCountArgName = Configuration.RetryCountArgName
                        let retryTimestampArgName = Configuration.RetryTimestampArgName

                        let channel =
                            state.Channel
                            |> Option.defaultWith (fun () ->
                                let connection = state.GetOrCreateConnection()
                                let channel = connection.CreateModel()
                                channel.ExchangeDeclare(exchange = ea.Exchange, ``type`` = "direct")
                                channel)

                        let newRetryCount = retryCount + 1
                        let retryTimestamp = getUtcNow () |> _.ToUnixTimeSeconds() |> AmqpTimestamp

                        ea.BasicProperties.Headers.Add(retryCountArgName, newRetryCount)
                        ea.BasicProperties.Headers.Add(retryTimestampArgName, retryTimestamp)

                        channel.BasicPublish(
                            exchange = ea.Exchange,
                            routingKey = ea.RoutingKey,
                            mandatory = true,
                            basicProperties = ea.BasicProperties,
                            body = ea.Body
                        )

                        state |> State.withChannel channel
                    |> loop
            }
    
        inbox
        |> initTopology
        |> loop)
    
    member _.Publish<'payload>(ev: Event<'payload>, priority: Priority) =
        let eventName = ev |> getEventName
        let boxedEvent = ev |> Event.mapPayload box

        (eventName, boxedEvent, priority) |> Message.Publish |> processor.Post

    interface IDisposable with
        member _.Dispose() = processor.Dispose()


[<RequireQualifiedAccess>]
module Agent =

    let private handleError (logger: ILogger<Agent>) : AgentErrorHandler<State, Message, IoError> =
        fun state msg err ->
            logger.LogError(
                "RabbitMQ Agent encountered an error processing message {Message} with error {Error}",
                msg,
                err
            )

            state |> AsyncOption.some

    let private dispose (state: State) =
        state.Consumer |> Option.map (_.Model >> _.Dispose()) |> ignore
        state.Channel |> Option.map _.Dispose() |> ignore
        state.GetOrCreateConnection() |> _.Dispose()
        Async.singleton ()

    let init connectionString = State.init connectionString

    let addHandler (handler: EventHandler<'payload, 'err>) state = state |> State.addHandler handler

    let build
        (logger: ILogger<Agent>)
        (getUtcNow: GetUtcNow)
        (options: Configuration.RabbitMQOptions)
        (jsonOptions: JsonSerializerOptions)
        (state: State)
        =
        Agent.init state
        |> Agent.withMessageHandler (handleMessage logger getUtcNow jsonOptions options)
        |> Agent.withErrorHandler (handleError logger)
        |> Agent.withDisposal dispose
        |> Agent.start
        |> fun agent ->
            agent.Post<Message> Init
            new Agent(agent)
