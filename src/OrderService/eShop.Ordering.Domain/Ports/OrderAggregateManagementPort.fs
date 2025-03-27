[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.OrderAggregateManagementPort

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Prelude

type ReadOrderAggregate<'ioError> = ReadAggregate<OrderAggregate.State, 'ioError>

type PersistOrderAggregate<'ioError> = PersistAggregate<OrderAggregate.State, 'ioError>

type PersistOrderAggregateEvents<'eventId, 'ioError> =
    PersistEvents<OrderAggregate.State, 'eventId, OrderAggregate.Event, 'ioError>

type PublishOrderAggregateEvents<'eventId, 'ioError> =
    PublishEvents<OrderAggregate.State, 'eventId, OrderAggregate.Event, 'ioError>

type GetSupportedCardTypes<'ioError> = unit -> AsyncResult<SupportedCardTypes, 'ioError>

type InvalidPaymentMethodError = InvalidPaymentMethodError

type VerifyPaymentMethod<'ioError> =
    UnverifiedPaymentMethod -> AsyncResult<VerifiedPaymentMethod, Either<InvalidPaymentMethodError, 'ioError>>
