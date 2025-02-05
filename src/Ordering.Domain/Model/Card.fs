namespace Ordering.Domain.Model

open FsToolkit.ErrorHandling
open Ordering.Domain.Model.ValueObjects

type Card = { Type: CardType; }

module CardType =
    let private cardTypeMap =
        Map.empty
        |> Map.add "Visa" Visa
        |> Map.add "MasterCard" MasterCard
        |> Map.add "Amex" Amex

    let toString cardType =
        cardTypeMap |> Map.findKey (fun _ v -> v = cardType)

    let ofString rawType =
        cardTypeMap
        |> Map.tryFind rawType
        |> Result.requireSome $"Invalid CardType: %s{rawType}"
