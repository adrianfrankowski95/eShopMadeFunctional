﻿namespace eShop.RabbitMQ

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

type MessageId = string
type MessageType = string

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

    let private consume (QueueName queueName) consumer (channel: IModel) =
        channel.BasicConsume(queue = queueName, autoAck = false, consumer = consumer)
        |> ignore

    let private ack (ea: BasicDeliverEventArgs) (channel: IModel) =
        channel.BasicAck(ea.DeliveryTag, multiple = false)

    let private nack
        (ea: BasicDeliverEventArgs)
        (logger: ILogger<RabbitMQEventDispatcher<'eventId, 'eventPayload>>)
        (channel: IModel)
        error
        =
        let messageId = ea.BasicProperties.MessageId
        let messageType = ea.BasicProperties.Type

        logger.LogError(
            "An error occurred for MessageId {MessageId} with type {MessageType}: {Error}",
            messageId,
            messageType,
            error
        )

        channel.BasicNack(ea.DeliveryTag, multiple = false, requeue = false)

    let internal initConsumerChannel (connection: IConnection) (config: Configuration.RabbitMQOptions) =
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
                |> List.traverseResultM ((|>) channel)
                |> Result.ignore

            channel.BasicQos(0u, 1us, false)

            let consumer = AsyncEventingBasicConsumer(channel)

            channel |> consume queueName consumer

            return consumer
        }

    let internal registerConsumer
        (eventNamesToConsume: EventName Set)
        (aggregateIdSelector: 'eventPayload -> AggregateId<'state>)
        (deserializeEvent: EventName -> string -> Result<'eventPayload, exn>)
        (consumer: AsyncEventingBasicConsumer)
        (config: Configuration.RabbitMQOptions)
        (logger: ILogger<RabbitMQEventDispatcher<'eventId, 'eventPayload>>)
        (persistEvents: PersistEvents<'state, 'eventId, 'eventPayload, _>)
        (processEvents: PublishEvents<'state, 'eventId, 'eventPayload, _>)      
        =
        result {
            let! queueName = config.SubscriptionClientName |> QueueName.create

            do!
                consumer.IsRunning
                |> Result.requireTrue $"""Consumer %s{consumer.ConsumerTags |> String.concat " "} is not running"""

            consumer.add_Received (fun _ ea ->
                asyncResult {
                    let timestamp =
                        ea.BasicProperties.Timestamp.UnixTime |> DateTimeOffset.FromUnixTimeSeconds

                    let! eventName =
                        ea.RoutingKey
                        |> EventName.create
                        |> Result.mapError (InvalidEventName >> Choice1Of3)

                    let! eventPayload =
                        Encoding.UTF8.GetString(ea.Body.Span)
                        |> deserializeEvent eventName
                        |> Result.mapError (DeserializationError >> Choice1Of3)

                    let aggregateId = eventPayload |> aggregateIdSelector

                    let! eventsWithIds =
                        [ { Data = eventPayload
                            OccurredAt = timestamp } ]
                        |> persistEvents aggregateId
                        |> AsyncResult.mapError Choice2Of3

                    do! eventsWithIds |> processEvents aggregateId |> AsyncResult.mapError Choice3Of3
                }
                |> AsyncResult.tee (fun _ -> ack ea consumer.Model)
                |> AsyncResult.teeError (nack ea logger consumer.Model)
                |> AsyncResult.ignoreError
                |> Async.StartImmediateAsTask
                :> Task)

            do!
                eventNamesToConsume
                |> Set.toList
                |> List.map (fun (EventName eventName) ->
                    bindQueue queueName eventName
                    >> Result.mapError (_.Message >> sprintf "Failed to bind RabbitMQ queue: %s"))
                |> List.traverseResultM ((|>) consumer.Model)
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
                properties.Type <- typeof<'eventPayload>.DeclaringType.Name + typeof<'eventPayload>.Name
                properties.DeliveryMode <- 2uy
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
