module eShop.RabbitMQ.Connection

open RabbitMQ.Client
open eShop.Prelude

let createChannel (connection: IConnection) =
    try
        connection.CreateModel() |> Ok
    with ex ->
        $"Failed to create RabbitMQ channel: %s{ex.Message}" |> Error

let ensureExchange (channel: IModel) =
    try
        channel.ExchangeDeclare(exchange = Configuration.ExchangeName, ``type`` = "direct")
        |> Ok
    with ex ->
        $"Failed to create RabbitMQ exchange: %s{ex.Message}" |> Error

let ensureDeadLetterExchange (channel: IModel) =
    try
        channel.ExchangeDeclare(
            exchange = Configuration.DeadLetter.ExchangeName,
            ``type`` = "direct",
            durable = true,
            autoDelete = false
        )
        |> Ok
    with ex ->
        $"Failed to create RabbitMQ exchange: %s{ex.Message}" |> Error

let ensureDeadLetterQueue (channel: IModel) =
    try
        channel.QueueDeclare(
            queue = Configuration.DeadLetter.QueueName,
            durable = true,
            exclusive = false,
            autoDelete = false,
            arguments = null
        )
        |> ignore
        |> Ok
    with ex ->
        $"Failed to create RabbitMQ queue: %s{ex.Message}" |> Error

let ensureQueue queueName (channel: IModel) =
    let arguments: Map<string, obj> =
        Map.empty
        |> Map.add "x-dead-letter-exchange" Configuration.DeadLetter.ExchangeName
        |> Map.add "x-dead-letter-routing-key" Configuration.DeadLetter.RoutingKey
        |> Map.mapValues box

    try
        channel.QueueDeclare(
            queue = queueName,
            durable = true,
            exclusive = false,
            autoDelete = false,
            arguments = arguments
        )
        |> ignore
        |> Ok
    with ex ->
        $"Failed to create RabbitMQ queue: %s{ex.Message}" |> Error

let bindDeadLetterQueue (channel: IModel) =
    try
        channel.QueueBind(
            Configuration.DeadLetter.QueueName,
            Configuration.DeadLetter.ExchangeName,
            Configuration.DeadLetter.RoutingKey
        )
        |> Ok
    with ex ->
        $"Failed to bind RabbitMQ queue: %s{ex.Message}" |> Error

let bindQueue queueName routingKey (channel: IModel) =
    try
        channel.QueueBind(queueName, Configuration.ExchangeName, routingKey) |> Ok
    with ex ->
        $"Failed to bind RabbitMQ queue: %s{ex.Message}" |> Error
