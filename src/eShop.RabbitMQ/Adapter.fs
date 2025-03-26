namespace eShop.RabbitMQ

open System
open RabbitMQ.Client
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

type RabbitMQDispatcher<'eventId, 'eventPayload> =
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

    let private createConnection (factory: ConnectionFactory) = Result.catch factory.CreateConnection

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

    let internal init
        (connectionFactory: ConnectionFactory)
        (config: Configuration.RabbitMqOptions)
        (eventsToConfigure: EventName Set)
        =
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

            use! connection =
                createConnection connectionFactory
                |> exnToMsg "Failed to create RabbitMQ connection"

            do!
                [ (1: float); 2; 5 ]
                |> List.map TimeSpan.FromSeconds
                |> ensureIsOpen connection
                |> AsyncResult.requireTrue "Connection to RabbitMQ was closed"

            use! channel = createChannel connection |> exnToMsg "Failed to create RabbitMQ channel"

            do!

                [ ensureDeadLetterExchange >> exnToMsg "Failed to declare RabbitMQ exchange"
                  ensureDeadLetterQueue >> exnToMsg "Failed to declare RabbitMQ queue"
                  bindDeadLetterQueue queueName >> exnToMsg "Failed to bind RabbitMQ queue"
                  ensureExchange >> exnToMsg "Failed to declare RabbitMQ exchange"
                  ensureQueue queueName >> exnToMsg "Failed to declare RabbitMQ queue" ]
                |> List.traverseResultM ((|>) channel)
                |> Result.ignore

            do!
                eventsToConfigure
                |> Set.toList
                |> List.map (fun (EventName eventName) ->
                    bindQueue queueName eventName >> exnToMsg "Failed to bind RabbitMQ queue")
                |> List.traverseResultM ((|>) channel)
                |> Result.ignore
        }

    let dispatchEvent
        (connection: IConnection)
        (eventName: EventName)
        (serializeEvent: 'eventPayload -> Result<byte array, exn>)
        : RabbitMQDispatcher<'eventId, 'eventPayload> =
        fun (eventId: 'eventId) (event: Event<'eventPayload>) ->
            asyncResult {
                let! body =
                    event.Data
                    |> serializeEvent
                    |> Result.map ReadOnlyMemory
                    |> Result.mapError SerializationError

                use! channel = createChannel connection |> Result.mapError ChannelCreationError

                do! channel |> ensureExchange |> Result.mapError ExchangeDeclarationError

                let properties = channel.CreateBasicProperties()
                properties.MessageId <- (eventId |> box |> string)
                properties.Type <- (typeof<'eventPayload>.DeclaringType.Name + typeof<'eventPayload>.Name)
                properties.DeliveryMode <- 2 |> byte
                properties.Timestamp <- AmqpTimestamp(event.OccurredAt.ToUnixTimeSeconds())
                properties.ContentType <- "application/json"
                properties.Persistent <- true

                return!
                    channel
                    |> publish eventName body properties
                    |> Result.mapError EventDispatchError
            }

//    let consumeEvent
//
// // let registerEventHandler<'T> (context: RabbitMQContext) (eventName: string) (handler: Consumer.EventHandler<'T>) =
// //     // Bind queue to exchange with routing key = eventName (exact match for direct exchange)
// //     Connection.bindQueue
// //         context.Channel
// //         context.Config.Consumer.QueueName
// //         context.Config.Consumer.ExchangeName
// //         eventName
// //
// //     // Setup consumer
// //     Consumer.setupConsumer<'T>
// //         context.Channel
// //         context.Config.Consumer.QueueName
// //         eventName
// //         handler
// //         context.Config.Consumer.PrefetchCount
//
// let publishEvent<'T> (context: RabbitMQContext) (eventName: string) (payload: 'T) (metadata: Map<string, string>) =
//     Publisher.publishEvent<'T> context.Channel Configuration.ExchangeName eventName payload metadata
//
// let shutdown (context: RabbitMQContext) =
//     try
//         if context.Channel.IsOpen then
//             context.Channel.Close()
//
//         if context.Connection.IsOpen then
//             context.Connection.Close()
//
//         Ok()
//     with ex ->
//         Error $"Failed to shutdown RabbitMQ context: %s{ex.Message}"
//


// this.Services
// .AddOpenTelemetry()
// .WithTracing(_.AddSource(Configuration.OpenTelemetry.ActivitySourceName) >> ignore)
// .Services.AddSingleton<Configuration.OpenTelemetry>(Configuration.OpenTelemetry.init)
