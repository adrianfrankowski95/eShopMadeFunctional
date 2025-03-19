﻿module eShop.ConstrainedTypes.Operators

open eShop.ConstrainedTypes

// A workaround for proper operator overload inference
type Operators = Operators
    with

        static member inline (*)(Operators, PositiveInt x) =
            Decimal.NonNegative.value
            >> (*) (decimal x)
            >> Decimal.NonNegative.createAbsolute

        static member inline (*)(Operators, NonNegativeDecimal x) =
            Int.Positive.value >> decimal >> (*) x >> Decimal.NonNegative.createAbsolute

let inline (*) x y = (Operators * x) y
