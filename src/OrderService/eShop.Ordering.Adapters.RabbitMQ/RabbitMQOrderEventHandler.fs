namespace eShop.Ordering.Adapters

open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.RabbitMQ
open eShop.Ordering.Domain.Model
open eShop.RabbitMQ

type RabbitMQOrderEventHandler<'eventId> = EventHandler<Order.State, 'eventId, Order.Event, RabbitMQIoError>

[<RequireQualifiedAccess>]
module RabbitMQOrderEventHandler =
    let create
        (eventDispatcher: RabbitMQEventDispatcher<'eventId, IntegrationEvent.Published>)
        : RabbitMQOrderEventHandler<'eventId> =
        fun aggregateId eventId event ->
            event
            |> Event.mapData (event.Data |> IntegrationEvent.Published.ofDomain aggregateId)
            |> eventDispatcher eventId
