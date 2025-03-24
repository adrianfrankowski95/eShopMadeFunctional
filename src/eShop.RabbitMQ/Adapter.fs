[<RequireQualifiedAccess>]
module eShop.RabbitMQ.Adapter

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open RabbitMQ.Client
open Microsoft.Extensions.DependencyInjection
open FsToolkit.ErrorHandling
open eShop.RabbitMQ
open eShop.ConstrainedTypes

type RabbitMQContext =
    { Connection: IConnection
      Channel: IModel
      Config: Configuration.RabbitMqOptions }

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
    let value (EventName name) = name

let initialize (connection: IConnection) (config: Configuration.RabbitMqOptions) (events: EventName Set) =
    asyncResult {
        let rec ensureIsOpen (retries: TimeSpan list) =
            match connection.IsOpen, retries with
            | true, _ -> true |> Async.retn
            | false, head :: tail ->
                async {
                    do! Async.Sleep head
                    return! ensureIsOpen tail
                }
            | false, [] -> false |> Async.retn

        do!
            [ (1: float); 2; 5 ]
            |> List.map TimeSpan.FromSeconds
            |> ensureIsOpen
            |> AsyncResult.requireTrue "Connection to RabbitMQ was not open"

        use! channel = Connection.createChannel connection

        // TODO: bind queue for each event type, enumerate union + IntegrationEvent

        do!

            [ Connection.ensureDeadLetterExchange
              Connection.ensureDeadLetterQueue
              Connection.bindDeadLetterQueue

              Connection.ensureExchange
              Connection.ensureQueue config.SubscriptionClientName ]
            |> List.traverseResultM ((|>) channel)
            |> Result.ignore

        do!
            events
            |> Set.toList
            |> List.map (EventName.value >> Connection.bindQueue config.SubscriptionClientName)
            |> List.traverseResultM ((|>) channel)
            |> Result.ignore

        return
            { Connection = connection
              Channel = channel
              Config = config }
    }
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
type IHostApplicationBuilder with
    member this.AddRabbitMq =
        fun connectionName events ->
            this.AddRabbitMQClient(
                connectionName,
                configureConnectionFactory = fun factory -> factory.DispatchConsumersAsync <- true
            )

            this.Services
            // .AddOpenTelemetry()
            // .WithTracing(_.AddSource(Configuration.OpenTelemetry.ActivitySourceName) >> ignore)
            // .Services.AddSingleton<Configuration.OpenTelemetry>(Configuration.OpenTelemetry.init)
                .AddSingleton<RabbitMQContext>
                    (fun sp ->
                        let config =
                            sp
                                .GetRequiredService<IConfiguration>()
                                .GetRequiredSection(Configuration.SectionName)
                                .Get<Configuration.RabbitMqOptions>()

                        let connection = sp.GetRequiredService<IConnection>()

                        initialize connection config events
                        |> Async.RunSynchronously
                        |> Result.valueOr failwith)
