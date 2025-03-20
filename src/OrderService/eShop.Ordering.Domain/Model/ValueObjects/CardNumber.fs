namespace eShop.Ordering.Domain.Model.ValueObjects

open eShop.ConstrainedTypes

type CardNumber = private CardNumber of string

[<RequireQualifiedAccess>]
module CardNumber =
    let create =
        [ String.Constraints.minLength 12; String.Constraints.maxLength 19 ]
        |> String.Constraints.evaluateM CardNumber (nameof CardNumber)

    let value (CardNumber rawNumber) = rawNumber
