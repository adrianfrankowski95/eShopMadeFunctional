namespace Ordering.Domain.Model

open eShop.ConstrainedTypes

[<Measure>]
type buyerId

type BuyerId = Id<buyerId>


type BuyerName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module BuyerName =
    let create = String.NonWhiteSpace.create (nameof BuyerName)


type Buyer = { Id: BuyerId; Name: BuyerName }
