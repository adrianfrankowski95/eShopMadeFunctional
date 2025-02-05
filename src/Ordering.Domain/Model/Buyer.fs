namespace Ordering.Domain.Model

open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign

type BuyerName = String.NonWhiteSpace

module BuyerName =
    let create = String.NonWhiteSpace.create (nameof BuyerName)

type Buyer =
    { Name: BuyerName
      PaymentMethods: Map<PaymentMethodId, PaymentMethod> }

module Buyer =
    let verifyOrAddPaymentMethod paymentMethodId paymentMethod buyer =
        let verifiedPaymentMethod =
            buyer.PaymentMethods
            |> Map.tryFind paymentMethodId
            |> Option.defaultValue paymentMethod
            |> PaymentMethod.verify

        buyer.PaymentMethods |> Map.add paymentMethodId verifiedPaymentMethod

type BuyerAggregate = Aggregate<Buyer>
