[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open System.Collections.Immutable
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open RabbitMQ.Client
open RabbitMQ.Client.Events
open FsToolkit.ErrorHandling
open eShop.RabbitMQ
open eShop.DomainDrivenDesign
open eShop.Prelude


type private Message =
    | Publish of EventName * Event<obj> * EventPriority
    | Retry of BasicDeliverEventArgs * RetryCount

module private Message =
    let getHighPriority (msg: Message) =
        match msg with
        | Publish(_, _, priority) -> if priority = High then Some msg else None
        | Retry _ -> None

type State =
    private
        { GetOrCreateConnection: unit -> IConnection
          Channel: IModel option
          ConsumerChannel: IModel option
          Consumer: AsyncEventingBasicConsumer option }

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

    let init createConnection =
        { GetOrCreateConnection = createConnection
          Channel = None
          ConsumerChannel = None
          Consumer = None }

    let withChannel channel state = { state with Channel = Some channel }

    let withConsumerChannel consumerChannel state =
        { state with
            ConsumerChannel = Some consumerChannel }

    let withConsumer consumer state = { state with Consumer = Some consumer }

type Agent internal (jsonOptions: JsonSerializerOptions, options: Configuration.EventBusOptions, getUtcNow: GetUtcNow, logger: ILogger<Agent>, connectionFactory: unit -> IConnection, consumer: AsyncEventingBasicConsumer, consumerChannel: IModel, eventHandlers) =
    let getEventName (ev: Event<'payload>) : string =
        let payloadType = typeof<'payload>

        match FSharpType.IsUnion(payloadType) with
        | true -> FSharpValue.GetUnionFields(ev.Data, payloadType) |> fst |> _.Name
        | false -> payloadType.Name

    let dispose (state: State) =
        state.Consumer |> Option.map (_.Model >> _.Dispose()) |> ignore
        state.Channel |> Option.map _.Dispose() |> ignore
        state.ConsumerChannel |> Option.map _.Dispose() |> ignore
        state.GetOrCreateConnection() |> _.Dispose()
    
    let cts = new CancellationTokenSource()
    
    let registerHandlers (inbox: MailboxProcessor<Message>)
        =
        let queueName = options.SubscriptionClientName
        let maxRetryCount = options.RetryCount
        let exchangeName = Configuration.MainExchangeName

        eventHandlers
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
                    eventHandlers
                    |> Map.tryFind eventName
                    |> Option.map (fun (eventType, handler) ->
                        taskResult {
                            let! payload =
                                bodyBytes
                                |> Json.deserializeBody jsonOptions eventType
                                |> Result.mapError Choice1Of2

                            return!
                                
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
    
    let processor = MailboxProcessor<Message>.Start((fun inbox ->
        let rec loop (state: State) =
            async {
                let! cancellationRequested = Async.CancellationToken |> Async.map _.IsCancellationRequested
                
                if cancellationRequested then
                    state |> dispose
                    return ()
                
                let! maybeHighPriority = inbox.TryScan(Message.getHighPriority >> Option.map Async.singleton)

                let! msg =
                    maybeHighPriority
                    |> Option.map Async.singleton
                    |> Option.defaultWith inbox.Receive

                return!
                    match msg with
                    | Publish(eventName, event, _) ->
                        let exchangeName = Configuration.MainExchangeName

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
        
        inbox |> registerHandlers
        
        State.init connectionFactory
        |> State.withConsumer consumer
        |> State.withConsumerChannel consumerChannel
        |> loop), cts.Token)

    //member _.Start(initState: State)
    
    member _.Publish<'payload>(ev: Event<'payload>, priority: EventPriority) =
        let eventName = ev |> getEventName
        let boxedEvent = ev |> Event.mapPayload box

        (eventName, boxedEvent, priority) |> Message.Publish |> processor.Post

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel(false)
            cts.Dispose()
            processor.Dispose()

[<RequireQualifiedAccess>]
module Agent =
    type private Options =
        { CreateConnection: unit -> IConnection
          EventHandlers: Map<EventName, EventType * BoxedEventHandler>
          SubscriptionClientName: string
          RetryCount: int
          MessageTtl: TimeSpan
          JsonOptions: JsonSerializerOptions
          Logger: ILogger<Agent>
          GetUtcNow: GetUtcNow }

    

    

    let private handleError (logger: ILogger<Agent>) : AgentErrorHandler<State, Message, IoError> =
        fun state msg err ->
            logger.LogError(
                "RabbitMQ Agent encountered an error processing message {Message} with error {Error}",
                msg,
                err
            )

            state |> AsyncOption.some

    let init connectionString (options: Configuration.EventBusOptions) logger getUtcNow  =
        { CreateConnection = createConnectionFactory connectionString
          RetryCount = options.RetryCount
          SubscriptionClientName = options.SubscriptionClientName
          MessageTtl = options.MessageTtl
          EventHandlers = Map.empty
          JsonOptions = JsonSerializerOptions.Default
          Logger = logger
          GetUtcNow = getUtcNow }

    let withJsonOptions (jsonOptions: JsonSerializerOptions) options =
        { options with
            JsonOptions = jsonOptions }
    
    let addHandler (handler: EventHandler<'payload, 'err>) options =
        let eventTypes = getEventTypes<'payload>

        let boxedHandler (ev: Event<obj>) : TaskResult<unit, obj> =
            ev |> Event.mapPayload unbox<'payload> |> handler |> TaskResult.mapError box

        let newHandlers =
            eventTypes
            |> List.fold
                (fun handlers (eventName, eventType) -> handlers |> Map.add eventName (eventType, boxedHandler))
                options.EventHandlers

        { options with
            EventHandlers = newHandlers }

    let private initTopology (options: Options) =
        let queueName = options.SubscriptionClientName

        let exchangeName = Configuration.MainExchangeName
        let dlExchangeName = Configuration.MainDeadLetterExchangeName
        let dlQueueName = Configuration.MainDeadLetterQueueName

        let dlExchangeArgName = Configuration.DeadLetterExchangeArgName
        let dlRoutingKeyArgName = Configuration.DeadLetterRoutingKeyArgName

        let connection = options.CreateConnection()

        let channel = connection.CreateModel()

        // Configure Dead Letter Exchange and Queue
        channel.ExchangeDeclare(exchange = dlExchangeName, ``type`` = "direct", durable = true, autoDelete = false)

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

        TaskResult.ok (connection, consumer, channel)
    
    let start (options: Options) =
        taskResult {
            let! connection, consumer, consumerChannel = initTopology options
            let proce
            
        }
        