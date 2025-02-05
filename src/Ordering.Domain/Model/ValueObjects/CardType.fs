namespace Ordering.Domain.Model.ValueObjects

open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes

type CardTypes = private CardTypes of Set<NonWhiteSpaceString>

type CardType = private CardType of NonWhiteSpaceString

module CardType =
    let create rawCardType (CardTypes cardTypes) =
        result {
            let! cardType =
                rawCardType
                |> NonWhiteSpaceString.create
                |> Result.mapError ((+) "Invalid Card Type: ")

            do!
                cardTypes
                |> Set.contains cardType
                |> Result.requireTrue $"Unsupported Card Type: %s{rawCardType}"

            return cardType |> CardType
        }
