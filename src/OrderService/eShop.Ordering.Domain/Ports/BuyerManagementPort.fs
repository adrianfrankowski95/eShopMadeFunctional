﻿[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.BuyerManagementPort

open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Model
open eShop.Prelude

type GetBuyer<'ioError> = BuyerId -> AsyncResult<Buyer option, 'ioError>

type GetSupportedCardTypes<'ioError> = unit -> AsyncResult<SupportedCardTypes, 'ioError>

type InvalidPaymentMethodError = InvalidPaymentMethodError

type VerifyPaymentMethod<'ioError> =
    UnverifiedPaymentMethod -> AsyncResult<VerifiedPaymentMethod, Either<InvalidPaymentMethodError, 'ioError>>
