namespace Ordering.Domain.Model.ValueObjects

open eShop.ConstrainedTypes

type CardHolderName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module CardHolderName =
    let create = String.NonWhiteSpace.create (nameof CardHolderName)
