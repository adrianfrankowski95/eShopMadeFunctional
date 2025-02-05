namespace Ordering.Domain.Model

open System
open Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes

type PaymentMethodData =
    { CardType: CardType
      CardNumber: NonWhiteSpaceString
      SecurityNumber: NonWhiteSpaceString
      CardHolderName: NonWhiteSpaceString
      Expiration: DateTimeOffset }

[<Struct; CustomEquality; CustomComparison>]
type PaymentMethod =
    | Pending of PaymentMethodData
    | Verified of PaymentMethodData

    override this.Equals other =
        match other with
        | :? PaymentMethod as other ->
            match this, other with
            | Pending data1, Pending data2
            | Pending data1, Verified data2
            | Verified data1, Pending data2
            | Verified data1, Verified data2 -> data1 = data2
        | _ -> false

    override this.GetHashCode() =
        match this with
        | Pending data
        | Verified data -> data.GetHashCode()

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? PaymentMethod as other ->
                match this, other with
                | Pending data1, Pending data2
                | Pending data1, Verified data2
                | Verified data1, Pending data2
                | Verified data1, Verified data2 -> compare data1 data2
            | _ -> -1

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
