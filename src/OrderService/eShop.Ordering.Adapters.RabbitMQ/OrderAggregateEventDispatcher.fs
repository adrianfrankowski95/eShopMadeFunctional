namespace eShop.Ordering.Adapters.RabbitMQ

open System.Text.Json
open RabbitMQ.Client
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.RabbitMQ

type OrderAggregateEventDispatcher = EventHandler<OrderAggregate.State, OrderAggregate.Event, RabbitMQIoError>

module OrderAggregateEventDispatcher =
    let create (jsonOptions: JsonSerializerOptions) (rabbitMQConnection: IConnection) : OrderAggregateEventDispatcher =
        fun aggregateId event ->
            let createEventName = IntegrationEvent.Published.createEventName
            let serializeEvent = jsonOptions |> IntegrationEvent.Published.serialize
            let eventPayload = event.Data |> IntegrationEvent.Published.ofDomain aggregateId

            event
            |> Event.mapPayload eventPayload
            |> RabbitMQ.createEventDispatcher createEventName serializeEvent rabbitMQConnection
