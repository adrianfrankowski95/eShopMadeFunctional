[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Ports.PaymentManagementPort

open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Model.ValueObjects
open FsToolkit.ErrorHandling

type GetSupportedCardTypes<'ioError> = unit -> TaskResult<SupportedCardTypes, 'ioError>

type VerificationResult =
    internal
    | Success of VerifiedPaymentMethod
    | Failed of UnverifiedPaymentMethod

[<RequireQualifiedAccess>]
module VerificationResult =
    let requireSuccess error result =
        match result with
        | Success verifiedPaymentMethod -> verifiedPaymentMethod |> Ok
        | Failed unverifiedPaymentMethod -> unverifiedPaymentMethod |> error |> Error

type VerifyPaymentMethod<'ioError> = UnverifiedPaymentMethod -> TaskResult<VerificationResult, 'ioError>
