namespace Ordering.Domain.Model

open eShop.ConstrainedTypes

[<Measure>]
type buyerId

type BuyerId = Id<buyerId>


type BuyerName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module BuyerName =
    let create = String.NonWhiteSpace.create (nameof BuyerName)


type Buyer =
    private
        { Id: BuyerId
          Name: BuyerName
          PaymentMethods: Map<PaymentMethodId, PaymentMethod> }

[<RequireQualifiedAccess>]
module Buyer =
    let getId = _.Id

    let getName = _.Name

    let getPaymentMethods = _.PaymentMethods

    let create id name =
        { Id = id
          Name = name
          PaymentMethods = Map.empty }

    let verifyOrAddPaymentMethod paymentMethodId paymentMethod buyer =
        let verifiedPaymentMethod =
            buyer.PaymentMethods
            |> Map.tryFind paymentMethodId
            |> Option.defaultValue paymentMethod
            |> PaymentMethod.verify

        buyer.PaymentMethods |> Map.add paymentMethodId verifiedPaymentMethod
