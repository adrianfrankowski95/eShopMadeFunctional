namespace Ordering.Domain.Model

open eShop.ConstrainedTypes

[<RequireQualifiedAccess>]
module DomainEvent =
    type OrderStarted = { Buyer: Buyer }

    type BuyerPaymentMethodVerified =
        { Buyer: BuyerWithVerifiedPaymentMethods
          VerifiedPaymentMethod: VerifiedPaymentMethod }

    type OrderCancelled = { Buyer: Buyer }

    type OrderStatusChangedToAwaitingValidation =
        { Buyer: BuyerWithVerifiedPaymentMethods
          StockToValidate: NonEmptyMap<ProductId, Units> }

    type OrderStockConfirmed =
        { Buyer: BuyerWithVerifiedPaymentMethods
          ConfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type OrderPaid =
        { PaidBy: BuyerWithVerifiedPaymentMethods
          PaidOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type OrderShipped =
        { ShippedTo: BuyerWithVerifiedPaymentMethods
          ShippedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

type DomainEvent =
    | OrderStarted of DomainEvent.OrderStarted
    | BuyerPaymentMethodVerified of DomainEvent.BuyerPaymentMethodVerified
    | OrderCancelled of DomainEvent.OrderCancelled
    | OrderStatusChangedToAwaitingValidation of DomainEvent.OrderStatusChangedToAwaitingValidation
    | OrderStockConfirmed of DomainEvent.OrderStockConfirmed
    | OrderPaid of DomainEvent.OrderPaid
    | OrderShipped of DomainEvent.OrderShipped
