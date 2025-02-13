[<RequireQualifiedAccess>]
module Ordering.Domain.Ports.BuyerManagementPort

open Ordering.Domain.Model.ValueObjects
open Ordering.Domain.Model
open eShop.Prelude

type GetBuyer<'ioError> = BuyerId -> AsyncResult<Buyer option, 'ioError>

type GetSupportedCardTypes<'ioError> = unit -> AsyncResult<SupportedCardTypes, 'ioError>

type InvalidPaymentMethod = UnverifiedPaymentMethod

type VerifyPaymentMethod<'ioError> =
    UnverifiedPaymentMethod -> AsyncResult<VerifiedPaymentMethod, Either<'ioError, InvalidPaymentMethod>>
