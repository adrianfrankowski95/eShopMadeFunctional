namespace eShop.Ordering.Adapters.RabbitMQ

open System.Text.Json
open RabbitMQ.Client
open eShop.Ordering.Adapters.Common
open eShop.RabbitMQ

type OrderIntegrationEventDispatcher = RabbitMQEventDispatcher<IntegrationEvent.Published>

module OrderIntegrationEventDispatcher =
    let create
        (jsonOptions: JsonSerializerOptions)
        (rabbitMQConnection: IConnection)
        : RabbitMQEventDispatcher<IntegrationEvent.Published> =
        let createEventName = IntegrationEvent.Published.getEventName
        let serializeEvent = jsonOptions |> IntegrationEvent.Published.serialize

        RabbitMQ.createEventDispatcher rabbitMQConnection createEventName serializeEvent
