[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.PaymentManagementPort

open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Prelude

type GetSupportedCardTypes<'ioError> = unit -> AsyncResult<SupportedCardTypes, 'ioError>

type InvalidPaymentMethodError = InvalidPaymentMethodError

type VerifyPaymentMethod<'ioError> =
    UnverifiedPaymentMethod -> AsyncResult<VerifiedPaymentMethod, Either<InvalidPaymentMethodError, 'ioError>>
