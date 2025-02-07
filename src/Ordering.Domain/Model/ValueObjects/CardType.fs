namespace Ordering.Domain.Model.ValueObjects

open FsToolkit.ErrorHandling
open eShop.ConstrainedTypes

type SupportedCardTypes = private SupportedCardTypes of Set<String.NonWhiteSpace>


type CardType = private CardType of String.NonWhiteSpace

[<RequireQualifiedAccess>]
module CardType =
    let create rawCardType (SupportedCardTypes supportedCardTypes) =
        result {
            let! cardType = rawCardType |> String.NonWhiteSpace.create (nameof CardType)

            return!
                supportedCardTypes
                |> Set.contains cardType
                |> Result.requireTrue $"Unsupported Card Type: %s{rawCardType}"
                |> Result.map (fun _ -> cardType |> CardType)
        }
