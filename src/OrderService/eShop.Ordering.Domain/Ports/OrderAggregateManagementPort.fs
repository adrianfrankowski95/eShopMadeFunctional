[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.OrderAggregateManagementPort

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type ReadOrderAggregate<'ioError> = ReadAggregate<OrderAggregate.State, 'ioError>

type PersistOrderAggregate<'ioError> = PersistAggregate<OrderAggregate.State, 'ioError>

type PersistOrderAggregateEvents<'eventId, 'ioError> =
    PersistEvents<OrderAggregate.State, 'eventId, OrderAggregate.Event, 'ioError>

type PublishOrderAggregateEvents<'ioError> = PublishEvents<OrderAggregate.State, OrderAggregate.Event, 'ioError>
