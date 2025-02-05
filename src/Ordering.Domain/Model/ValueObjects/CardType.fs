namespace Ordering.Domain.Model.ValueObjects

open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes

type CardTypes = private CardTypes of Set<String.NonWhiteSpace>

type CardType = private CardType of String.NonWhiteSpace

[<RequireQualifiedAccess>]
module CardType =
    let create rawCardType (CardTypes cardTypes) =
        result {
            let! cardType = rawCardType |> String.NonWhiteSpace.create (nameof CardType)

            return!
                cardTypes
                |> Set.contains cardType
                |> Result.requireTrue $"Unsupported Card Type: %s{rawCardType}"
                |> Result.map (fun _ -> cardType |> CardType)
        }
