[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open HCollections
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open RabbitMQ.Client
open RabbitMQ.Client.Events
open FsToolkit.ErrorHandling
open eShop.RabbitMQ
open eShop.DomainDrivenDesign
open eShop.Prelude
open TypeShape

// type EventName = string
//
// type Message =
//     private
//     | Publish of EventName * Event<obj>
//     | Subscribe of EventName * (Event<obj> -> TaskResult<unit, obj>)

type EventHandler<'payload, 'err> = Event<'payload> -> TaskResult<unit, 'err>

type RabbitMQMessage = interface end
//
// type Publish<'payload> = Publish of Event<'payload> interface RabbitMQMessage
// type Subscribe<'payload, 'err> = Subscribe of EventHandler<'payload, 'err> interface RabbitMQMessage

type Message<'payload, 'err> =
    | Publish of Event<'payload>
    | Subscribe of EventHandler<'payload, 'err>
    interface RabbitMQMessage

type State =
    private
        { Connection: IConnection option
          Channel: IModel option
          Consumer: AsyncEventingBasicConsumer option }

[<RequireQualifiedAccess>]
module private State =
    let empty =
        { Connection = None
          Channel = None
          Consumer = None }

    let withConnection connection state =
        { state with
            Connection = Some connection }

    let withChannel channel state = { state with Channel = Some channel }

    let withConsumer consumer state = { state with Consumer = Some consumer }

type IoError =
    | DeserializationError of exn
    | SerializationError of exn
    | ChannelCreationError of exn
    | ExchangeDeclarationError of exn
    | EventDispatchError of exn
    | InvalidEventData of string


type RabbitMQAgent =
    inherit IDisposable
    abstract Publish<'payload> : Event<'payload> -> ValueTask
    abstract Subscribe<'payload, 'err> : EventHandler<'payload, 'err> -> ValueTask

module RabbitMQAgent =
    let inline private getEventName<'t> : string = typeof<'t>.Name

    let create (agent: Agent<State, RabbitMQMessage, IoError>) =
        let inline boxEvent (event: Event<'payload>) : Event<obj> =
            { Data = event.Data |> box
              Id = event.Id
              OccurredAt = event.OccurredAt }

        { new RabbitMQAgent with
            member _.Publish<'payload>(event: Event<'payload>) : ValueTask =
                agent.Post(Publish(getEventName<'payload>, event |> boxEvent))

            member _.Subscribe<'payload, 'handlerErr>
                (handler: Event<'payload> -> TaskResult<unit, 'handlerErr>)
                : ValueTask =
                let boxedHandler =
                    fun ev ->
                        let boxedEvent = ev |> boxEvent

                        handler boxedEvent |> TaskResult.mapError box

                (getEventName<'payload>, boxedHandler) |> Subscribe |> agent.Post }

type Agent = Agent<State, RabbitMQMessage, IoError>

[<RequireQualifiedAccess>]
module Agent =
    let private createConsumer (connection: IConnection) queueName exchangeName dlExchangeName dlQueueName =
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

        channel.QueueBind(dlQueueName, dlExchangeName, queueName)

        // Configure Main Exchange and Queue
        channel.ExchangeDeclare(exchange = exchangeName, ``type`` = "direct")

        let arguments: Map<string, obj> =
            Map.empty
            |> Map.add "x-dead-letter-exchange" dlExchangeName
            |> Map.add "x-dead-letter-routing-key" queueName
            |> Map.mapValues box

        channel.QueueDeclare(
            queue = queueName,
            durable = true,
            exclusive = false,
            autoDelete = false,
            arguments = arguments
        )
        |> ignore

        channel.BasicQos(0u, 1us, false)

        let consumer = AsyncEventingBasicConsumer(channel)

        channel.BasicConsume(queue = queueName, autoAck = false, consumer = consumer)
        |> ignore

        consumer

    let private handleMessage
        (connectionString: string)
        (jsonOptions: JsonSerializerOptions)
        (options: Configuration.RabbitMQOptions)
        (getUtcNow: GetUtcNow)
        (logger: ILogger<Agent>)
        : AgentMessageHandler<State, RabbitMQMessage, IoError> =
        let connectionFactory =
            ConnectionFactory(
                Uri = Uri(connectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10.0),
                RequestedHeartbeat = TimeSpan.FromSeconds(10.0),
                DispatchConsumersAsync = true
            )

        fun state msg ->
            task {
                let exchangeName = Configuration.ExchangeName

                let connection =
                    state.Connection |> Option.defaultWith connectionFactory.CreateConnection

                match msg with
                | Publish event ->
                    let eventName = event |> getEventName

                    let body =
                        JsonSerializer.SerializeToUtf8Bytes(event.Data, jsonOptions) |> ReadOnlyMemory

                    let channel =
                        state.Channel
                        |> Option.defaultWith (fun () ->
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

                    channel.BasicPublish(
                        exchange = exchangeName,
                        routingKey = eventName,
                        mandatory = true,
                        basicProperties = properties,
                        body = body
                    )

                    return
                        state
                        |> State.withConnection connection
                        |> State.withChannel channel
                        |> ResultOption.singleton

                | Subscribe handler ->
                    let queueName = options.SubscriptionClientName
                    let exchangeName = Configuration.ExchangeName
                    let dlExchangeName = Configuration.DeadLetterExchangeName
                    let dlQueueName = Configuration.DeadLetterQueueName

                    let consumer =
                        state.Consumer
                        |> Option.defaultWith (fun () ->
                            createConsumer connection queueName exchangeName dlExchangeName dlQueueName)

                    consumer.Model.QueueBind(queueName, exchangeName, RabbitMQ.EventName.value eventName)

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

                            let payload = Encoding.UTF8.GetString(ea.Body.Span)

                            do!
                                (eventName,
                                 { Id = eventId
                                   Data = payload
                                   OccurredAt = timestamp })
                                |> handler
                                |> TaskResult.mapError Choice2Of2
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

                            consumer.Model.BasicNack(ea.DeliveryTag, multiple = false, requeue = false))
                        |> TaskResult.ignoreError
                        :> Task)

                    return
                        state
                        |> State.withConnection connection
                        |> State.withConsumer consumer
                        |> ResultOption.singleton
            }

    let private handleError (logger: ILogger<Agent>) : AgentErrorHandler<State, Message, IoError> =
        fun state msg err ->
            logger.LogError(
                "RabbitMQ Agent encountered an error processing message {Message} with error {Error}",
                msg,
                err
            )

            state |> TaskOption.some

    let private dispose (state: State) =
        state.Consumer |> Option.map (_.Model >> _.Dispose()) |> ignore
        state.Channel |> Option.map _.Dispose() |> ignore
        state.Connection |> Option.map _.Dispose() |> ignore
        Task.singleton ()

    let build
        (connectionString: string)
        (logger: ILogger<Agent>)
        (getUtcNow: GetUtcNow)
        (options: Configuration.RabbitMQOptions)
        (jsonOptions: JsonSerializerOptions)
        =
        State.empty
        |> Agent.init
        |> Agent.withMessageHandler (handleMessage connectionString jsonOptions options getUtcNow logger)
        |> Agent.withErrorHandler (handleError logger)
        |> Agent.withDisposal dispose
        |> Agent.start
        |> fun agent ->
            { new RabbitMQAgent with
                member _.Publish<'payload>(event: Event<'payload>) : ValueTask =
                    let boxedEvent: Event<obj> = { event with Data = event.Data :> obj }

                    publish boxedEvent agent |> ValueTask

                member _.Subscribe<'payload, 'handlerErr>
                    (handler: Event<'payload> -> TaskResult<unit, 'handlerErr>)
                    : ValueTask =
                    let boxedHandler (event: Event<obj>) : TaskResult<unit, obj> =
                        let payload = event.Data :?> 'payload

                        let typedEvent: Event<'payload> = { event with Data = payload }

                        handler typedEvent |> TaskResult.mapError box

                    subscribe Configuration.EventName boxedHandler agent |> ValueTask }

    let publish (event: Event<obj>) (agent: Agent) = event |> Publish |> agent.Post

    let subscribe (eventName: RabbitMQ.EventName) (handler: Event<obj> -> TaskResult<unit, obj>) (agent: Agent) =
        agent.Post(Subscribe(eventName, handler))
