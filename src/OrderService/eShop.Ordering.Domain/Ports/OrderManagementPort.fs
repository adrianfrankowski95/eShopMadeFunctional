[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.OrderManagementPort

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Prelude

type ReadOrderAggregate<'ioError> = ReadAggregate<Order.State, 'ioError>

type PersistOrderAggregate<'ioError> = PersistAggregate<Order.State, 'ioError>

type PersistOrderEvents<'eventId, 'ioError> = PersistEvents<Order.State, 'eventId, Order.Event, 'ioError>

type PublishOrderEvents<'eventId, 'ioError> = PublishEvents<Order.State, 'eventId, Order.Event, 'ioError>

type GetSupportedCardTypes<'ioError> = unit -> AsyncResult<SupportedCardTypes, 'ioError>

type InvalidPaymentMethodError = InvalidPaymentMethodError

type VerifyPaymentMethod<'ioError> =
    UnverifiedPaymentMethod -> AsyncResult<VerifiedPaymentMethod, Either<InvalidPaymentMethodError, 'ioError>>
