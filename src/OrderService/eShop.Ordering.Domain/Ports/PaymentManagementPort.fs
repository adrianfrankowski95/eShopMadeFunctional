[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.PaymentManagementPort

open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Prelude
open FsToolkit.ErrorHandling

type GetSupportedCardTypes<'ioError> = unit -> TaskResult<SupportedCardTypes, 'ioError>

type InvalidPaymentMethodError = InvalidPaymentMethodError

type VerifyPaymentMethod<'ioError> =
    UnverifiedPaymentMethod -> TaskResult<VerifiedPaymentMethod, Either<InvalidPaymentMethodError, 'ioError>>
