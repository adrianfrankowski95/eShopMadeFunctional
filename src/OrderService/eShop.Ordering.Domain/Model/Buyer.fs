namespace eShop.Ordering.Domain.Model

open eShop.ConstrainedTypes

[<Measure>]
type userId

type UserId = Id<userId>


type BuyerName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module BuyerName =
    let create = String.NonWhiteSpace.create (nameof BuyerName)

    let value = String.NonWhiteSpace.value

type Buyer = { Id: UserId; Name: BuyerName }
