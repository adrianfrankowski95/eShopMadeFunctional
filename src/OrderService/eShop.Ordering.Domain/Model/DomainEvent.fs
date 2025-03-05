namespace eShop.Ordering.Domain.Model

open Microsoft.FSharp.Core
open eShop.ConstrainedTypes

[<RequireQualifiedAccess>]
module DomainEvent =
    type OrderStarted = { Buyer: Buyer }

    type PaymentMethodVerified =
        { Buyer: Buyer
          VerifiedPaymentMethod: VerifiedPaymentMethod }

    type OrderCancelled = { Buyer: Buyer }

    type OrderStatusChangedToAwaitingValidation =
        { Buyer: Buyer
          StockToValidate: NonEmptyMap<ProductId, Units> }

    type OrderStockConfirmed =
        { Buyer: Buyer
          ConfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type OrderPaid =
        { Buyer: Buyer
          PaidOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type OrderShipped =
        { Buyer: Buyer
          ShippedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

type DomainEvent =
    internal
    | OrderStarted of DomainEvent.OrderStarted
    | PaymentMethodVerified of DomainEvent.PaymentMethodVerified
    | OrderCancelled of DomainEvent.OrderCancelled
    | OrderStatusChangedToAwaitingValidation of DomainEvent.OrderStatusChangedToAwaitingValidation
    | OrderStockConfirmed of DomainEvent.OrderStockConfirmed
    | OrderPaid of DomainEvent.OrderPaid
    | OrderShipped of DomainEvent.OrderShipped
