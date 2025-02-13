namespace Ordering.Domain.Model

open eShop.ConstrainedTypes
open eShop.ConstrainedTypes.Int.ActivePatterns
open eShop.ConstrainedTypes.Decimal.ActivePatterns
open eShop.ConstrainedTypes.Operators
open FsToolkit.ErrorHandling

[<Measure>]
type productId

type ProductId = Id<productId>


type ProductName = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module ProductName =
    let create = String.NonWhiteSpace.create (nameof ProductName)


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


type PictureUrl = string

type TotalPrice = Decimal.NonNegative

type DiscountHigherThanTotalPrice = DiscountHigherThanTotalPrice of (TotalPrice * Discount)

type OrderItemWithConfirmedStock =
    private
        { ProductName: ProductName
          PictureUrl: PictureUrl
          UnitPrice: UnitPrice
          Units: Units
          Discount: Discount }

type OrderItemWithUnconfirmedStock =
    private
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

    let create productName pictureUrl (unitPrice: UnitPrice) (units: Units) (discount: Discount) =
        result {
            let totalPrice = unitPrice * units

            do!
                totalPrice < discount
                |> Result.requireFalse ((totalPrice, discount) |> DiscountHigherThanTotalPrice)

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
            unvalidatedOrderItem.PictureUrl
            unvalidatedOrderItem.UnitPrice
            unvalidatedOrderItem.Units
            unvalidatedOrderItem.Discount

type OrderItem =
    | Unvalidated of UnvalidatedOrderItem
    | WithUnconfirmedStock of OrderItemWithUnconfirmedStock
    | WithConfirmedStock of OrderItemWithConfirmedStock
