[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.OrderAggregateManagementPort

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type ReadOrderAggregate<'ioError> = ReadAggregate<Order.State, 'ioError>

type PersistOrderAggregate<'ioError> = PersistAggregate<Order.State, 'ioError>

type PersistOrderAggregateEvents<'ioError> = PersistEvents<Order.State, Order.Event, 'ioError>

type PublishOrderAggregateEvents<'ioError> = PublishEvents<Order.State, Order.Event, 'ioError>