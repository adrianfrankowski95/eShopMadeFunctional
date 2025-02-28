namespace eShop.Ordering.Domain.Model.ValueObjects

open eShop.ConstrainedTypes

type CardSecurityNumber = private CardSecurityNumber of string

[<RequireQualifiedAccess>]
module CardSecurityNumber =
    let create =
        String.Constraints.fixedLength 3 CardSecurityNumber (nameof CardSecurityNumber)
