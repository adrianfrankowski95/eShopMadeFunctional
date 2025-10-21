namespace eShop.Ordering.Domain.Model

open eShop.ConstrainedTypes
open eShop.ConstrainedTypes.Operators
open FsToolkit.ErrorHandling
open FSharp.UMX

[<Measure>]
type product

type ProductId = int<product>

[<RequireQualifiedAccess>]
module ProductId =
    let ofInt (int: int) : ProductId = %int

    let value (id: ProductId) : int = %id
    
    let toString = value >> string


type ProductName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module ProductName =
    let create = String.NonWhiteSpace.create (nameof ProductName)

    let value = String.NonWhiteSpace.value


type UnitPrice = Decimal.NonNegative

[<RequireQualifiedAccess>]
module UnitPrice =
    let create = Decimal.NonNegative.create (nameof UnitPrice)

    let value (NonNegativeDecimal value) = value


type Units = Int.Positive

[<RequireQualifiedAccess>]
module Units =
    let create = Int.Positive.create (nameof Units)

    let value (PositiveInt value) = value


type Discount = Decimal.NonNegative

[<RequireQualifiedAccess>]
module Discount =
    let create = Decimal.NonNegative.create (nameof Discount)

    let value (NonNegativeDecimal value) = value


type PictureUrl = string option

type OrderItemWithConfirmedStock =
    internal
        { ProductName: ProductName
          PictureUrl: PictureUrl
          UnitPrice: UnitPrice
          Units: Units
          Discount: Discount }

type TotalPrice = Decimal.NonNegative

type DiscountHigherThanTotalPriceError = DiscountHigherThanTotalPriceError of (TotalPrice * Discount)

type OrderItemWithUnconfirmedStock =
    internal
        { ProductName: ProductName
          PictureUrl: PictureUrl
          UnitPrice: UnitPrice
          Units: Units
          Discount: Discount }

[<RequireQualifiedAccess>]
module OrderItemWithUnconfirmedStock =
    let getProductName = _.ProductName

    let getPictureUrl = _.PictureUrl

    let getUnitPrice = _.UnitPrice

    let getUnits = _.Units

    let create productName (unitPrice: UnitPrice) (units: Units) (discount: Discount) pictureUrl =
        result {
            let totalPrice = unitPrice * units

            do!
                totalPrice < discount
                |> Result.requireFalse ((totalPrice, discount) |> DiscountHigherThanTotalPriceError)

            return
                { ProductName = productName
                  PictureUrl = pictureUrl
                  UnitPrice = unitPrice
                  Units = units
                  Discount = discount }
        }

    let confirmStock orderItem : OrderItemWithConfirmedStock =
        { ProductName = orderItem.ProductName
          PictureUrl = orderItem.PictureUrl
          UnitPrice = orderItem.UnitPrice
          Units = orderItem.Units
          Discount = orderItem.Discount }

type UnvalidatedOrderItem =
    { ProductName: ProductName
      PictureUrl: PictureUrl
      UnitPrice: UnitPrice
      Units: Units
      Discount: Discount }

[<RequireQualifiedAccess>]
module UnvalidatedOrderItem =
    let create productName pictureUrl unitPrice units discount =
        { ProductName = productName
          PictureUrl = pictureUrl
          UnitPrice = unitPrice
          Units = units
          Discount = discount }

    let validate (unvalidatedOrderItem: UnvalidatedOrderItem) =
        OrderItemWithUnconfirmedStock.create
            unvalidatedOrderItem.ProductName
            unvalidatedOrderItem.UnitPrice
            unvalidatedOrderItem.Units
            unvalidatedOrderItem.Discount
            unvalidatedOrderItem.PictureUrl

type OrderItem =
    | Unvalidated of UnvalidatedOrderItem
    | WithUnconfirmedStock of OrderItemWithUnconfirmedStock
    | WithConfirmedStock of OrderItemWithConfirmedStock

[<RequireQualifiedAccess>]
module OrderItem =
    let getProductName =
        function
        | Unvalidated unvalidated -> unvalidated.ProductName
        | WithUnconfirmedStock withUnconfirmedStock -> withUnconfirmedStock.ProductName
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.ProductName

    let getUnitPrice =
        function
        | Unvalidated unvalidated -> unvalidated.UnitPrice
        | WithUnconfirmedStock withUnconfirmedStock -> withUnconfirmedStock.UnitPrice
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.UnitPrice

    let getUnits =
        function
        | Unvalidated unvalidated -> unvalidated.Units
        | WithUnconfirmedStock withUnconfirmedStock -> withUnconfirmedStock.Units
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Units

    let getDiscount =
        function
        | Unvalidated unvalidated -> unvalidated.Discount
        | WithUnconfirmedStock withUnconfirmedStock -> withUnconfirmedStock.Discount
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Discount

    let getPictureUrl =
        function
        | Unvalidated unvalidated -> unvalidated.PictureUrl
        | WithUnconfirmedStock withUnconfirmedStock -> withUnconfirmedStock.PictureUrl
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.PictureUrl
