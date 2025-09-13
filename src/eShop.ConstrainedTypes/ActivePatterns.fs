namespace eShop.ConstrainedTypes

[<AutoOpen>]
module ActivePatterns =
    let (|NonNegativeDecimal|) = Decimal.NonNegative.value
    
    let (|PositiveInt|) = Int.Positive.value

    let (|NonNegativeInt|) = Int.NonNegative.value

    let (|NonEmptyList|) = NonEmptyList.toList
    
    let (|NonEmptyMap|) = NonEmptyMap.toMap
    
    let (|NonWhiteSpaceString|) = String.NonWhiteSpace.value