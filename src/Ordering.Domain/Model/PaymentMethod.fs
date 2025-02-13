namespace Ordering.Domain.Model

open System
open Ordering.Domain.Model.ValueObjects

type UnverifiedPaymentMethod =
    { CardType: CardType
      CardNumber: CardNumber
      SecurityNumber: CardSecurityNumber
      CardHolderName: CardHolderName
      Expiration: DateTimeOffset }

[<RequireQualifiedAccess>]
module UnverifiedPaymentMethod =
    let create cardType cardNumber securityNumber cardHolderName expiration =
        { CardType = cardType
          CardNumber = cardNumber
          SecurityNumber = securityNumber
          CardHolderName = cardHolderName
          Expiration = expiration }

// Note: After verification, remove properties that are no longer needed or should not be stored anywhere
type VerifiedPaymentMethod =
    private
        { CardType: CardType
          CardNumber: CardNumber
          Expiration: DateTimeOffset }
