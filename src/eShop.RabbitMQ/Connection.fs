module eShop.RabbitMQ.Connection

open RabbitMQ.Client
open System

type ConnectionFactory with
    static member fromConfig(config: ConnectionConfig) =
        let factory = ConnectionFactory()
        factory.HostName <- config.HostName
        factory.Port <- config.Port
        factory.UserName <- config.UserName
        factory.Password <- config.Password
        factory.VirtualHost <- config.VirtualHost

        if config.SslEnabled then
            factory.Ssl <- SslOption(Enabled = true)

        factory

let createConnection (config: ConnectionConfig) =
    try
        let factory = ConnectionFactory.fromConfig config
        Ok(factory.CreateConnection())
    with ex ->
        Error(sprintf "Failed to create RabbitMQ connection: %s" ex.Message)

let createChannel (connection: IConnection) =
    try
        Ok(connection.CreateModel())
    with ex ->
        Error(sprintf "Failed to create RabbitMQ channel: %s" ex.Message)

let ensureExchange (channel: IModel) exchangeName exchangeType durable autoDelete =
    channel.ExchangeDeclare(exchangeName, exchangeType, durable, autoDelete, null)

let ensureQueue (channel: IModel) queueName deadLetterExchange =
    let arguments =
        match deadLetterExchange with
        | Some exchange ->
            let args = Collections.Generic.Dictionary<string, obj>()
            args.Add("x-dead-letter-exchange", box exchange)
            args
        | None -> null

    channel.QueueDeclare(queueName, true, false, false, arguments) |> ignore

let bindQueue (channel: IModel) queueName exchangeName routingKey =
    channel.QueueBind(queueName, exchangeName, routingKey)
