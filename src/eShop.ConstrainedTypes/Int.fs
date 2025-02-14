[<RequireQualifiedAccess>]
module eShop.ConstrainedTypes.Int

open System
open Microsoft.FSharp.Core
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Constraints =
    let nonNegative ctor fieldName rawInt =
        rawInt < 0
        |> Result.requireFalse $"%s{fieldName} is invalid: Expected non-negative value, actual value: %d{rawInt}"
        |> Result.map (fun _ -> rawInt |> ctor)

    let positive ctor fieldName rawInt =
        rawInt < 1
        |> Result.requireFalse $"%s{fieldName} is invalid: Expected positive value, actual value: %d{rawInt}"
        |> Result.map (fun _ -> rawInt |> ctor)

type Positive = private Positive of int

type NonNegative = private NonNegative of int

[<RequireQualifiedAccess>]
module Positive =
    let create = Constraints.positive Positive

    let add (NonNegative value1) (Positive value2) = value1 + value2 |> Positive

    let value (Positive int) = int

[<RequireQualifiedAccess>]
module NonNegative =
    let create = Constraints.nonNegative NonNegative

    let createAbsolute: int -> NonNegative = Math.Abs >> NonNegative

    let ofPositiveInt (Positive int) = int |> NonNegative

    let value (NonNegative int) = int
