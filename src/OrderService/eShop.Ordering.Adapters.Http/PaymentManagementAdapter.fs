[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.PaymentManagementAdapter

open eShop.Ordering.Adapters.Http
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open FsToolkit.ErrorHandling
open eShop.Prelude

// Dummy implementation
let verifyPaymentMethod shouldAcceptPayment : PaymentManagementPort.VerifyPaymentMethod<HttpIoError> =
    fun unverifiedPayment ->
        match shouldAcceptPayment with
        | true -> unverifiedPayment |> UnverifiedPaymentMethod.verify |> TaskResult.ok
        | false -> PaymentManagementPort.InvalidPaymentMethodError |> Left |> TaskResult.error
