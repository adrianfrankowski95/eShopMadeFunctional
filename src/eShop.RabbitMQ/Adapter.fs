// [<RequireQualifiedAccess>]
// module eShop.RabbitMQ
//
// open System
// open RabbitMQ.Client
// open FsToolkit.ErrorHandling
// open RabbitMQ.Client.Events
// open eShop.ConstrainedTypes
//
// [<Literal>]
// let private ExchangeName = "eshop_event_bus"
//
// type QueueName = private QueueName of string
//
// module QueueName =
//     let create =
//         [ String.Constraints.nonWhiteSpace
//           String.Constraints.hasMaxUtf8Bytes 255
//           String.Constraints.doesntStartWith "amqp." ]
//         |> String.Constraints.evaluateM QueueName (nameof QueueName)
//
// let configure (rabbitMqConnection: IConnection) (QueueName queueName) events =
//     let rec ensureIsOpen (retries: TimeSpan list) =
//         match rabbitMqConnection.IsOpen, retries with
//         | true, _ -> true |> Async.retn
//         | false, head :: tail ->
//             async {
//                 do! Async.Sleep head
//                 return! ensureIsOpen tail
//             }
//         | false, [] -> false |> Async.retn
//
//     asyncResult {
//         do!
//             [ (1: float); 2; 5 ]
//             |> List.map TimeSpan.FromSeconds
//             |> ensureIsOpen
//             |> AsyncResult.requireTrue "Connection to RabbitMQ was not open"
//
//         let channel = rabbitMqConnection.CreateModel()
//
//         channel.ExchangeDeclare(ExchangeName, ``type`` = "direct")
//
//         channel.QueueDeclare(queue = queueName, durable = true, exclusive = false, autoDelete = false)
//         |> ignore
//
//         let consumer = AsyncEventingBasicConsumer(channel)
//
//         //channel.
//
//         return failwith ""
//     }
//     |> AsyncResult.ignoreError

module eShop.RabbitMQ.Adapter

open RabbitMQ.Client
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration

type RabbitMQContext =
    { Connection: IConnection
      Channel: IModel
      Config: RabbitMQConfig }

let initialize (config: RabbitMQConfig) =
    let connectionResult = Connection.createConnection config.Connection

    match connectionResult with
    | Ok connection ->
        let channelResult = Connection.createChannel connection

        match channelResult with
        | Ok channel ->
            // Ensure exchange exists
            Connection.ensureExchange
                channel
                config.Publisher.ExchangeName
                config.Publisher.ExchangeType
                config.Publisher.Durable
                config.Publisher.AutoDelete

            // Ensure dead letter exchange if configured
            match config.Consumer.DeadLetterExchange with
            | Some dlx -> Connection.ensureExchange channel dlx "fanout" true false
            | None -> ()

            // Ensure queue exists
            Connection.ensureQueue channel config.Consumer.QueueName config.Consumer.DeadLetterExchange

            Ok
                { Connection = connection
                  Channel = channel
                  Config = config }
        | Error e -> Error e
    | Error e -> Error e

let registerEventHandler<'T> (context: RabbitMQContext) (eventName: string) (handler: Consumer.EventHandler<'T>) =
    // Bind queue to exchange with routing key = eventName (exact match for direct exchange)
    Connection.bindQueue
        context.Channel
        context.Config.Consumer.QueueName
        context.Config.Consumer.ExchangeName
        eventName

    // Setup consumer
    Consumer.setupConsumer<'T>
        context.Channel
        context.Config.Consumer.QueueName
        eventName
        handler
        context.Config.Consumer.PrefetchCount

let publishEvent<'T> (context: RabbitMQContext) (eventName: string) (payload: 'T) (metadata: Map<string, string>) =
    Publisher.publishEvent<'T> context.Channel context.Config.Publisher.ExchangeName eventName payload metadata

let shutdown (context: RabbitMQContext) =
    try
        if context.Channel.IsOpen then
            context.Channel.Close()

        if context.Connection.IsOpen then
            context.Connection.Close()

        Ok()
    with ex ->
        Error(sprintf "Failed to shutdown RabbitMQ context: %s" ex.Message)

// Extension methods for dependency injection
type IServiceCollection with
    member this.AddRabbitMQ(configuration: IConfiguration, serviceName: string) =
        let config = Configuration.getConfig configuration serviceName

        this
            .AddSingleton<RabbitMQConfig>(config)
            .AddSingleton<RabbitMQContext>(fun sp ->
                let config = sp.GetRequiredService<RabbitMQConfig>()

                match initialize config with
                | Ok context -> context
                | Error e -> failwith e)
