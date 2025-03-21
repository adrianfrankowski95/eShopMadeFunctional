module eShop.RabbitMQ.Publisher

open RabbitMQ.Client
open System.Text
open System.Text.Json
open System

let publishEvent<'T>
    (channel: IModel)
    (exchangeName: string)
    (eventName: string)
    (payload: 'T)
    (metadata: Map<string, string>)
    =
    try
        let eventMessage =
            { Id = Guid.NewGuid()
              CorrelationId = None
              CreatedAt = DateTimeOffset.UtcNow
              EventName = eventName
              Payload = payload
              Metadata = metadata }

        let json = JsonSerializer.Serialize(eventMessage)
        let body = Encoding.UTF8.GetBytes(json)

        let properties = channel.CreateBasicProperties()
        properties.MessageId <- eventMessage.Id.ToString()
        properties.ContentType <- "application/json"
        properties.Persistent <- true

        // With direct exchange, the routing key must exactly match the binding key (eventName)
        channel.BasicPublish(
            exchange = exchangeName,
            routingKey = eventName, // Using eventName as the exact routing key
            basicProperties = properties,
            body = ReadOnlyMemory<byte>(body)
        )

        Ok eventMessage.Id
    with ex ->
        Error(sprintf "Failed to publish event: %s" ex.Message)
