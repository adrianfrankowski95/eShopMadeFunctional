namespace eShop.Ordering.Domain.Model

open eShop.ConstrainedTypes

[<Measure>]
type user

type UserId = Id<user>


type BuyerName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module BuyerName =
    let create = String.NonWhiteSpace.create (nameof BuyerName)

    let value = String.NonWhiteSpace.value

type Buyer = { Id: UserId; Name: BuyerName }
