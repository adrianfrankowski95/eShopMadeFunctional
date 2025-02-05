namespace Ordering.Domain.Model

open eShop.ConstrainedTypes
open eShop.ConstrainedTypes.Int.ActivePatterns
open eShop.ConstrainedTypes.Decimal.ActivePatterns
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

    let add value (units: Units) = Int.Positive.add value units

    let value (PositiveInt value) = value


type Discount = Decimal.NonNegative

[<RequireQualifiedAccess>]
module Discount =
    let create = Decimal.NonNegative.create (nameof Discount)

    let value (NonNegativeDecimal value) = value


type PictureUrl = string

type TotalPrice = Decimal.NonNegative

type DiscountHigherThanTotalPriceError = DiscountHigherThanTotalPriceError of (TotalPrice * Discount)


type OrderItem =
    private
        { ProductId: ProductId
          ProductName: ProductName
          PictureUrl: PictureUrl
          UnitPrice: UnitPrice
          Units: Units
          Discount: Discount }

[<RequireQualifiedAccess>]
module OrderItem =
    let getProductId orderItem = orderItem.ProductId

    let getProductName orderItem = orderItem.ProductName

    let getPictureUrl orderItem = orderItem.PictureUrl

    let getUnitPrice orderItem = orderItem.UnitPrice

    let getUnits orderItem = orderItem.Units

    let create productId productName pictureUrl (unitPrice: UnitPrice) (units: Units) (discount: Discount) =
        result {
            let rawUnitPrice = unitPrice |> UnitPrice.value
            let rawDiscount = discount |> Discount.value

            let totalPrice = units |> Units.value |> decimal |> ((*) rawUnitPrice)

            do!
                totalPrice < rawDiscount
                |> Result.requireFalse (
                    (totalPrice |> Decimal.NonNegative.createAbsolute, discount)
                    |> DiscountHigherThanTotalPriceError
                )

            return
                { ProductId = productId
                  ProductName = productName
                  PictureUrl = pictureUrl
                  UnitPrice = unitPrice
                  Units = units
                  Discount = discount }
        }

    let setNewDiscount discount orderItem = { orderItem with Discount = discount }

    let addUnits units orderItem =
        { orderItem with
            Units = orderItem.Units |> Units.add units }
