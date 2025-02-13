namespace Ordering.Domain.Model.ValueObjects

open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes

type CardTypeId = CardTypeId of int

[<RequireQualifiedAccess>]
module CardTypeId =
    let value (CardTypeId value) = value

type CardTypeName = String.NonWhiteSpace

type CardType = private CardType of CardTypeId * CardTypeName

type SupportedCardTypes = private SupportedCardTypes of Map<CardTypeId, CardTypeName>


[<RequireQualifiedAccess>]
module CardType =
    let create (SupportedCardTypes supportedCardTypes) cardTypeId =
        supportedCardTypes
        |> Map.tryFind cardTypeId
        |> Result.requireSome $"Invalid CardTypeId: %d{cardTypeId |> CardTypeId.value}"
        |> Result.map (fun cardTypeName -> (cardTypeId, cardTypeName) |> CardType)
