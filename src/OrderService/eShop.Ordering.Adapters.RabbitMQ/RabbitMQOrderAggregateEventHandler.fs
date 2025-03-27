namespace eShop.Ordering.Adapters

open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.RabbitMQ

type RabbitMQOrderAggregateEventHandler<'eventId> =
    EventHandler<OrderAggregate.State, 'eventId, OrderAggregate.Event, RabbitMQIoError>

[<RequireQualifiedAccess>]
module RabbitMQOrderAggregateEventHandler =
    let create
        (dispatchEvent: RabbitMQEventDispatcher<'eventId, IntegrationEvent.Published>)
        : RabbitMQOrderAggregateEventHandler<'eventId> =
        fun aggregateId eventId event ->
            event
            |> Event.mapData (event.Data |> IntegrationEvent.Published.ofDomain aggregateId)
            |> dispatchEvent eventId
