namespace eShop.Ordering.Domain.Model.ValueObjects

open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes
open FSharp.UMX

[<Measure>]
type cardTypeId

type CardTypeId = int<cardTypeId>

[<RequireQualifiedAccess>]
module CardTypeId =
    let ofInt (int: int) : CardTypeId = %int


type CardTypeName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module CardTypeName =
    let create = String.NonWhiteSpace.create (nameof CardTypeName)

type SupportedCardTypes = internal SupportedCardTypes of Map<CardTypeId, CardTypeName>


type UnsupportedCardTypeError = UnsupportedCardTypeError of CardTypeId

type CardType =
    internal
        { Id: CardTypeId
          Name: CardTypeName }

[<RequireQualifiedAccess>]
module CardType =
    let create (SupportedCardTypes supportedCardTypes) cardTypeId =
        supportedCardTypes
        |> Map.tryFind cardTypeId
        |> Result.requireSome (cardTypeId |> UnsupportedCardTypeError)
        |> Result.map (fun cardTypeName -> { Id = cardTypeId; Name = cardTypeName })
