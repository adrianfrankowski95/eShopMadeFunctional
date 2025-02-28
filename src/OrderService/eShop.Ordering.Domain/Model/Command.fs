namespace eShop.Ordering.Domain.Model

open System
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes

[<RequireQualifiedAccess>]
module Command =
    type CreateOrderDraft =
        { BuyerId: BuyerId
          OrderItems: Map<ProductId, UnvalidatedOrderItem> }

    type CreateOrder =
        { Buyer: Buyer
          Address: Address
          PaymentMethod: VerifiedPaymentMethod
          OrderedAt: DateTimeOffset
          OrderItems: NonEmptyMap<ProductId, OrderItemWithUnconfirmedStock> }

    type SetStockRejectedOrderStatus =
        { RejectedOrderItems: NonEmptyList<ProductId> }

type Command =
    | CreateOrderDraft of Command.CreateOrderDraft
    | CreateOrder of Command.CreateOrder
    | SetStockConfirmedOrderStatus
    | SetStockRejectedOrderStatus of Command.SetStockRejectedOrderStatus
    | SetPaidOrderStatus
    | ShipOrder
    | CancelOrder
