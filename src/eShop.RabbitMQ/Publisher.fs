[<RequireQualifiedAccess>]
module internal eShop.RabbitMQ.Publisher

open RabbitMQ.Client
open System.Text
open System.Text.Json
open System
open eShop.DomainDrivenDesign

let publishEvent<'T>
    (channel: IModel)
    (eventName: string)
    (eventId: string)
    (event: Event<'T>)
    (metadata: Map<string, string>)
    =
    try
        let json = JsonSerializer.Serialize(event)
        let body = Encoding.UTF8.GetBytes(json)

        let properties = channel.CreateBasicProperties()
        properties.MessageId <- eventId
        properties.Timestamp <- AmqpTimestamp(event.OccurredAt.ToUnixTimeSeconds())
        properties.ContentType <- "application/json"
        properties.Persistent <- true

        channel.BasicPublish(
            exchange = Configuration.ExchangeName,
            routingKey = eventName,
            basicProperties = properties,
            body = ReadOnlyMemory<byte>(body)
        )

        Ok()
    with ex ->
        Error(sprintf "Failed to publish event: %s" ex.Message)
