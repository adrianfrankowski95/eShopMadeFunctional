namespace eShop.Ordering.Adapters.RabbitMQ

open System.Text.Json
open RabbitMQ.Client
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.RabbitMQ

type OrderAggregateIntegrationEventDispatcher<'eventId> =
    EventHandler<OrderAggregate.State, 'eventId, OrderAggregate.Event, RabbitMQIoError>

module OrderAggregateIntegrationEventDispatcher =
    let create
        (jsonOption: JsonSerializerOptions)
        (rabbitMQConnection: IConnection)
        : OrderAggregateIntegrationEventDispatcher<'eventId> =
        fun aggregateId eventId event ->
            let createEventName = IntegrationEvent.Published.createEventName
            let serializeEvent = jsonOption |> IntegrationEvent.Published.serialize
            let eventPayload = event.Data |> IntegrationEvent.Published.ofDomain aggregateId

            event
            |> Event.mapPayload eventPayload
            |> RabbitMQ.createEventDispatcher createEventName serializeEvent rabbitMQConnection eventId
