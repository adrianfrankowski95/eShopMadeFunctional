namespace eShop.RabbitMQ

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open RabbitMQ.Client
open RabbitMQ.Client.Events
open FsToolkit.ErrorHandling
open eShop.RabbitMQ
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.Prelude

type RabbitMQIoError =
    | DeserializationError of exn
    | SerializationError of exn
    | ChannelCreationError of exn
    | ExchangeDeclarationError of exn
    | EventDispatchError of exn
    | InvalidEventData of string

type RabbitMQEventDispatcher<'eventPayload> = Event<'eventPayload> -> TaskResult<unit, RabbitMQIoError>

[<RequireQualifiedAccess>]
module RabbitMQ =
    type QueueName = private QueueName of string

    module QueueName =
        let create =
            [ String.Constraints.nonWhiteSpace
              String.Constraints.hasMaxUtf8Bytes 255
              String.Constraints.doesntStartWith "amqp." ]
            |> String.Constraints.evaluateM QueueName (nameof QueueName)

    type EventName = private EventName of string

    module EventName =
        let create = String.Constraints.nonWhiteSpace EventName (nameof EventName)

        let value (EventName eventName) = eventName

    let internal createChannel (connection: IConnection) = Result.catch connection.CreateModel

    let internal ensureExchange exchangeName (channel: IModel) =
        Result.catch (fun () ->
            channel.ExchangeDeclare(exchange = exchangeName, ``type`` = "direct")
            channel)

    let internal ensureDeadLetterExchange (channel: IModel) =
        Result.catch (fun () ->
            channel.ExchangeDeclare(
                exchange = Configuration.DeadLetterExchangeName,
                ``type`` = "direct",
                durable = true,
                autoDelete = false
            ))

    let internal ensureDeadLetterQueue (channel: IModel) =
        Result.catch (fun () ->
            channel.QueueDeclare(
                queue = Configuration.DeadLetterQueueName,
                durable = true,
                exclusive = false,
                autoDelete = false,
                arguments = null
            )
            |> ignore)

    let internal ensureQueue (QueueName queueName) (channel: IModel) =
        let arguments: Map<string, obj> =
            Map.empty
            |> Map.add "x-dead-letter-exchange" Configuration.DeadLetterExchangeName
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
