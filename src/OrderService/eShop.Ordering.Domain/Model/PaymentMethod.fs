namespace eShop.Ordering.Domain.Model

open System
open eShop.Ordering.Domain.Model.ValueObjects
open FsToolkit.ErrorHandling

type PaymentMethodExpiredError = PaymentMethodExpiredError

type UnverifiedPaymentMethod =
    private
        { CardType: CardType
          CardNumber: CardNumber
          CardSecurityNumber: CardSecurityNumber
          CardHolderName: CardHolderName
          CardExpiration: DateTimeOffset }

[<RequireQualifiedAccess>]
module UnverifiedPaymentMethod =
    let create cardType cardNumber securityNumber cardHolderName expiration now =
        result {
            do! expiration > now |> Result.requireTrue PaymentMethodExpiredError

            return
                { CardType = cardType
                  CardNumber = cardNumber
                  CardSecurityNumber = securityNumber
                  CardHolderName = cardHolderName
                  CardExpiration = expiration }
        }


// Note: After verification, remove properties that are no longer needed or should not be stored anywhere
type VerifiedPaymentMethod =
    internal
        { CardType: CardType
          CardNumber: CardNumber
          CardHolderName: CardHolderName
          CardExpiration: DateTimeOffset }