[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.OrderManagementPort

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Prelude

type ReadOrderAggregate<'ioError> = ReadAggregate<Order, 'ioError>

type PersistOrderAggregate<'ioError> = PersistAggregate<Order, 'ioError>

type PersistOrderEvents<'eventId, 'ioError> = PersistEvents<Order, 'eventId, DomainEvent, 'ioError>

type PublishOrderEvents<'eventId, 'ioError> = PublishEvents<'eventId, DomainEvent, 'ioError>

type ReadUnprocessedOrderEvents<'eventId, 'ioError when 'eventId: comparison> =
    ReadUnprocessedEvents<'eventId, DomainEvent, 'ioError>

type GetSupportedCardTypes<'ioError> = unit -> AsyncResult<SupportedCardTypes, 'ioError>
