[<RequireQualifiedAccess>]
module eShop.ConstrainedTypes.Decimal

open Microsoft.FSharp.Core
open FsToolkit.ErrorHandling
open System

module Constraints =
    let nonNegative ctor fieldName (rawDecimal: decimal) =
        rawDecimal < 0.0m
        |> Result.requireFalse $"%s{fieldName} is invalid: Expected non-negative value, actual value: %f{rawDecimal}"
        |> Result.map (fun _ -> rawDecimal |> ctor)


type NonNegative = private NonNegative of decimal

module NonNegative =
    let create = Constraints.nonNegative NonNegative

    let createAbsolute: decimal -> NonNegative = Math.Abs >> NonNegative

    let value (NonNegative value) = value

[<AutoOpen>]
module ActivePatterns =
    let (|NonNegativeDecimal|) = NonNegative.value
