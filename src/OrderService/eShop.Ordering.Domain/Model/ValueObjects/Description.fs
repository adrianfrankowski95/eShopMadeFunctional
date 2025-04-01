namespace eShop.Ordering.Domain.Model.ValueObjects

open eShop.ConstrainedTypes

type Description = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module Description =
    let create = String.NonWhiteSpace.create (nameof Description)

    let value = String.NonWhiteSpace.value
