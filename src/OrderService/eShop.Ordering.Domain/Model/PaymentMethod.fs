namespace eShop.Ordering.Domain.Model

open System
open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model.ValueObjects
open FsToolkit.ErrorHandling

type PaymentMethodExpiredError = PaymentMethodExpiredError

// Note: After verification, remove properties that are no longer needed or should not be stored anywhere
type VerifiedPaymentMethod =
    internal
        { CardType: CardType
          CardNumber: CardNumber
          CardHolderName: CardHolderName
          CardExpiration: DateOnly }


type UnverifiedPaymentMethod =
    private
        { CardType: CardType
          CardNumber: CardNumber
          CardSecurityNumber: CardSecurityNumber
          CardHolderName: CardHolderName
          CardExpiration: DateOnly }

[<RequireQualifiedAccess>]
module UnverifiedPaymentMethod =
    let create cardType cardNumber securityNumber cardHolderName (expiration: DateOnly) (now: UtcNow) =
        result {
            do!
                now.Date
                |> DateOnly.FromDateTime
                |> ((>) expiration)
                |> Result.requireTrue PaymentMethodExpiredError

            return
                { CardType = cardType
                  CardNumber = cardNumber
                  CardSecurityNumber = securityNumber
                  CardHolderName = cardHolderName
                  CardExpiration = expiration }
        }

    let internal verify (paymentMethod: UnverifiedPaymentMethod) : VerifiedPaymentMethod =
        { CardExpiration = paymentMethod.CardExpiration
          CardNumber = paymentMethod.CardNumber
          CardHolderName = paymentMethod.CardHolderName
          CardType = paymentMethod.CardType }

type PaymentMethod =
    | Unverified of UnverifiedPaymentMethod
    | Verified of VerifiedPaymentMethod