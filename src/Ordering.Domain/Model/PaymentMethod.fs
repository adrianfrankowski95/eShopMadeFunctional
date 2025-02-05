namespace Ordering.Domain.Model

open System
open Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes

[<Measure>]
type paymentMethodId

type PaymentMethodId = Id<paymentMethodId>

type PaymentMethodData =
    { CardType: CardType
      CardNumber: CardNumber
      SecurityNumber: CardSecurityNumber
      CardHolderName: CardHolderName
      Expiration: DateTimeOffset }

[<Struct>]
type PaymentMethod =
    private
    | Pending of PaymentMethodData
    | Verified of PaymentMethodData

module PaymentMethod =
    let getCardType =
        function
        | Pending data -> data.CardType
        | Verified data -> data.CardType

    let getCardNumber =
        function
        | Pending data -> data.CardNumber
        | Verified data -> data.CardNumber

    let getSecurityNumber =
        function
        | Pending data -> data.SecurityNumber
        | Verified data -> data.SecurityNumber

    let getCardHolderName =
        function
        | Pending data -> data.CardHolderName
        | Verified data -> data.CardHolderName

    let getExpiration =
        function
        | Pending data -> data.Expiration
        | Verified data -> data.Expiration

    let create data = data |> Pending

    let verify =
        function
        | Pending data -> data |> Verified
        | Verified data -> data |> Verified
