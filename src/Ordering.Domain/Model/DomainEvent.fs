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

    type T =
        | OrderStarted of OrderStarted
        | BuyerPaymentMethodVerified of BuyerPaymentMethodVerified
        | OrderCancelled of OrderCancelled
        | OrderStatusChangedToAwaitingValidation of OrderStatusChangedToAwaitingValidation
        | OrderStockConfirmed of OrderStockConfirmed
        | OrderPaid of OrderPaid
        | OrderShipped of OrderShipped

type DomainEvent = DomainEvent.T
