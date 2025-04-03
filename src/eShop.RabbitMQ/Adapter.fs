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
    | InvalidEventName of string

type RabbitMQEventDispatcher<'eventId, 'eventPayload> =
    'eventId -> Event<'eventPayload> -> AsyncResult<unit, RabbitMQIoError>

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


    let private createChannel (connection: IConnection) = Result.catch connection.CreateModel

    let private ensureExchange (channel: IModel) =
        Result.catch (fun () -> channel.ExchangeDeclare(exchange = Configuration.ExchangeName, ``type`` = "direct"))

    let private ensureDeadLetterExchange (channel: IModel) =
        Result.catch (fun () ->
            channel.ExchangeDeclare(
                exchange = Configuration.DeadLetterExchangeName,
                ``type`` = "direct",
                durable = true,
                autoDelete = false
            ))

    let private ensureDeadLetterQueue (channel: IModel) =
        Result.catch (fun () ->
            channel.QueueDeclare(
                queue = Configuration.DeadLetterQueueName,
                durable = true,
                exclusive = false,
                autoDelete = false,
                arguments = null
            )
            |> ignore)

    let private ensureQueue (QueueName queueName) (channel: IModel) =
        Result.catch (fun () ->
            let arguments: Map<string, obj> =
                Map.empty
                |> Map.add "x-dead-letter-exchange" Configuration.DeadLetterExchangeName
                |> Map.add "x-dead-letter-routing-key" queueName
                |> Map.mapValues box

            channel.QueueDeclare(
                queue = queueName,
                durable = true,
                exclusive = false,
                autoDelete = false,
                arguments = arguments
            )
            |> ignore)

    let private bindDeadLetterQueue (QueueName queueName) (channel: IModel) =
        Result.catch (fun () ->
            channel.QueueBind(Configuration.DeadLetterQueueName, Configuration.DeadLetterExchangeName, queueName))

    let private bindQueue (QueueName queueName) routingKey (channel: IModel) =
        Result.catch (fun () -> channel.QueueBind(queueName, Configuration.ExchangeName, routingKey))

    let private publish (EventName eventName) body properties (channel: IModel) =
        Result.catch (fun () ->
            channel.BasicPublish(
                exchange = Configuration.ExchangeName,
                routingKey = eventName,
                mandatory = true,
                basicProperties = properties,
                body = body
            ))

    let private createConsumer (QueueName queueName) (channel: IModel) =
        Result.catch (fun () ->
            channel.BasicQos(0u, 1us, false)

            let consumer = AsyncEventingBasicConsumer(channel)

            channel.BasicConsume(queue = queueName, autoAck = false, consumer = consumer)
            |> ignore

            consumer)

    let private ack (ea: BasicDeliverEventArgs) (channel: IModel) =
        channel.BasicAck(ea.DeliveryTag, multiple = false)

    let private nack (ea: BasicDeliverEventArgs) (logger: ILogger<'eventPayload>) (channel: IModel) error =
        logger.LogError(
            "An error occurred for MessageId {MessageId} of type {MessageType}: {Error}",
            ea.BasicProperties.MessageId,
            ea.BasicProperties.Type,
            error
        )

        channel.BasicNack(ea.DeliveryTag, multiple = false, requeue = false)

    let internal initConsumer (connection: IConnection) (config: Configuration.RabbitMQOptions) =
        asyncResult {
            let rec ensureIsOpen (connection: IConnection) (retries: TimeSpan list) =
                match connection.IsOpen, retries with
                | true, _ -> true |> Async.retn
                | false, head :: tail ->
                    async {
                        do! Async.Sleep head
                        return! ensureIsOpen connection tail
                    }
                | false, [] -> false |> Async.retn

            let inline exnToMsg msg : Result<_, exn> -> Result<_, string> =
                Result.mapError (_.Message >> sprintf "%s: %s" msg)

            let! queueName = config.SubscriptionClientName |> QueueName.create

            do!
                [ (1: float); 2; 5 ]
                |> List.map TimeSpan.FromSeconds
                |> ensureIsOpen connection
                |> AsyncResult.requireTrue "Connection to RabbitMQ was closed"

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
        (deserializeEvent: EventName -> string -> Result<'eventPayload, exn>)
        (consumer: AsyncEventingBasicConsumer)
        (config: Configuration.RabbitMQOptions)
        (logger: ILogger<'eventPayload>)
        (getUtcNow: GetUtcNow)
        (processEvents: PublishEvents<'state, 'eventPayload, 'ioError>)
        =
        result {
            let! queueName = config.SubscriptionClientName |> QueueName.create

            do!
                consumer.IsRunning
                |> Result.requireTrue $"""Consumer %s{consumer.ConsumerTags |> String.concat " "} is not running"""

            consumer.add_Received (fun _ ea ->
                asyncResult {
                    let timestamp =
                        ea.BasicProperties.Timestamp
                        |> Option.ofNull
                        |> Option.map (_.UnixTime >> DateTimeOffset.FromUnixTimeSeconds)
                        |> Option.defaultWith getUtcNow

                    let! eventName =
                        ea.RoutingKey
                        |> EventName.create
                        |> Result.mapError (InvalidEventName >> Choice1Of2)

                    let! eventPayload =
                        Encoding.UTF8.GetString(ea.Body.Span)
                        |> deserializeEvent eventName
                        |> Result.mapError (DeserializationError >> Choice1Of2)

                    let aggregateId = eventPayload |> aggregateIdSelector

                    do!
                        { Data = eventPayload
                          OccurredAt = timestamp }
                        |> List.singleton
                        |> processEvents aggregateId
                        |> AsyncResult.mapError Choice2Of2
                }
                |> AsyncResult.tee (fun _ -> ack ea consumer.Model)
                |> AsyncResult.teeError (nack ea logger consumer.Model)
                |> AsyncResult.ignoreError
                |> Async.StartImmediateAsTask
                :> Task)

            do!
                eventNamesToHandle
                |> Set.toList
                |> List.traverseResultA (fun (EventName eventName) -> consumer.Model |> bindQueue queueName eventName)
                |> Result.mapError (
                    List.map _.Message
                    >> String.concat Environment.NewLine
                    >> (+) "Failed to bind RabbitMQ queues: %s"
                )
                |> Result.ignore
        }

    let createEventDispatcher
        (createEventName: 'eventPayload -> Result<EventName, string>)
        (serializeEvent: 'eventPayload -> Result<byte array, exn>)
        (connection: IConnection)
        : RabbitMQEventDispatcher<'eventId, 'eventPayload> =
        fun (eventId: 'eventId) (event: Event<'eventPayload>) ->
            asyncResult {
                let! body =
                    event.Data
                    |> serializeEvent
                    |> Result.map ReadOnlyMemory
                    |> Result.mapError SerializationError

                let! eventName = event.Data |> createEventName |> Result.mapError InvalidEventName

                use! channel = createChannel connection |> Result.mapError ChannelCreationError

                do! channel |> ensureExchange |> Result.mapError ExchangeDeclarationError

                let properties = channel.CreateBasicProperties()
                properties.MessageId <- (eventId |> box |> string)
                properties.Type <- Event.typeName<'eventPayload>
                properties.DeliveryMode <- 2uy
                properties.Timestamp <- AmqpTimestamp(event.OccurredAt.ToUnixTimeSeconds())
                properties.ContentType <- "application/json"
                properties.Persistent <- true

                return!
                    channel
                    |> publish eventName body properties
                    |> Result.mapError EventDispatchError
            }
