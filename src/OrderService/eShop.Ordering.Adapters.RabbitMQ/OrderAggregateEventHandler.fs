namespace eShop.Ordering.Adapters

open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.RabbitMQ

type OrderAggregateEventHandler<'eventId> =
    EventHandler<OrderAggregate.State, 'eventId, OrderAggregate.Event, RabbitMQIoError>

[<RequireQualifiedAccess>]
module OrderAggregateEventHandler =
    let create
        (dispatchEvent: RabbitMQEventDispatcher<'eventId, IntegrationEvent.Published>)
        : OrderAggregateEventHandler<'eventId> =
        fun aggregateId eventId event ->
            event
            |> Event.mapData (event.Data |> IntegrationEvent.Published.ofDomain aggregateId)
            |> dispatchEvent eventId
