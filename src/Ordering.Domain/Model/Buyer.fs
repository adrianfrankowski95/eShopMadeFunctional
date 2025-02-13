namespace Ordering.Domain.Model

open eShop.ConstrainedTypes

[<Measure>]
type buyerId

type BuyerId = Id<buyerId>


type BuyerName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module BuyerName =
    let create = String.NonWhiteSpace.create (nameof BuyerName)


type BuyerWithoutVerifiedPaymentMethods =
    private { Id: BuyerId; Name: BuyerName }

type BuyerWithVerifiedPaymentMethods =
    private
        { Id: BuyerId
          Name: BuyerName
          PaymentMethods: VerifiedPaymentMethod Set }

type Buyer =
    private
    | WithoutVerifiedPaymentMethods of BuyerWithoutVerifiedPaymentMethods
    | WithVerifiedPaymentMethods of BuyerWithVerifiedPaymentMethods

[<RequireQualifiedAccess>]
module Buyer =
    let private getId =
        function
        | WithoutVerifiedPaymentMethods buyer -> buyer.Id
        | WithVerifiedPaymentMethods buyer -> buyer.Id

    let private getName =
        function
        | WithoutVerifiedPaymentMethods buyer -> buyer.Name
        | WithVerifiedPaymentMethods buyer -> buyer.Name

    let private getPaymentMethods =
        function
        | WithoutVerifiedPaymentMethods _ -> Set.empty
        | WithVerifiedPaymentMethods buyer -> buyer.PaymentMethods

    let create name id =
        { Id = id; Name = name } |> WithoutVerifiedPaymentMethods

    let verifyOrAddPaymentMethod verifiedPaymentMethod buyer =
        { Id = buyer |> getId
          Name = buyer |> getName
          PaymentMethods = buyer |> getPaymentMethods |> Set.add verifiedPaymentMethod }
