namespace Ordering.Domain.Model

open System
open Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes

type OrderStateError =
    | OnlyPaidOrderCanBeShipped
    | PaidOrderCannotBeCancelled
    | ShippedOrderCannotBeCancelled

[<RequireQualifiedAccess>]
module Order =
    type Draft =
        { BuyerId: BuyerId
          UnvalidatedOrderItems: Map<ProductId, UnvalidatedOrderItem> }

    type AwaitingStockValidation =
        { Buyer: BuyerWithVerifiedPaymentMethods
          Address: Address
          OrderDate: DateTimeOffset
          UnconfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithUnconfirmedStock> }

    type WithConfirmedStock =
        { Buyer: BuyerWithVerifiedPaymentMethods
          Address: Address
          OrderDate: DateTimeOffset
          ConfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Paid =
        { Buyer: BuyerWithVerifiedPaymentMethods
          Address: Address
          OrderDate: DateTimeOffset
          PaidOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Shipped =
        { Buyer: BuyerWithVerifiedPaymentMethods
          Address: Address
          OrderDate: DateTimeOffset
          ShippedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Cancelled =
        { Buyer: Buyer
          Address: Address
          OrderDate: DateTimeOffset
          CancelledOrderItems: NonEmptyMap<ProductId, OrderItem> }

    type T =
        | Init
        | AwaitingStockValidation of AwaitingStockValidation
        | WithConfirmedStock of WithConfirmedStock
        | Paid of Paid
        | Shipped of Shipped
        | Cancelled of Cancelled

    let evolve (state: T) (command: Command) : Result<T * DomainEvent list, OrderStateError> =
        match state, command with
        | Init, CreateOrder cmd ->
            let buyerWithVerifiedPaymentMethod =
                cmd.Buyer |> Buyer.verifyOrAddPaymentMethod cmd.VerifiedPaymentMethod

            let newState =
                { Buyer = buyerWithVerifiedPaymentMethod
                  Address = cmd.Address
                  OrderDate = cmd.OrderDate
                  UnconfirmedOrderItems = cmd.OrderItems }

            let events =
                [ ({ Buyer = cmd.Buyer }: DomainEvent.OrderStarted) |> DomainEvent.OrderStarted
                  ({ Buyer = buyerWithVerifiedPaymentMethod
                     VerifiedPaymentMethod = cmd.VerifiedPaymentMethod }
                  : DomainEvent.BuyerPaymentMethodVerified)
                  |> DomainEvent.BuyerPaymentMethodVerified
                  ({ Buyer = buyerWithVerifiedPaymentMethod
                     StockToValidate =
                       newState.UnconfirmedOrderItems
                       |> NonEmptyMap.map (fun _ orderItem -> orderItem.Units) }
                  : DomainEvent.OrderStatusChangedToAwaitingValidation)
                  |> DomainEvent.OrderStatusChangedToAwaitingValidation ]

            (newState |> T.AwaitingStockValidation, events) |> Ok

        | AwaitingStockValidation awaitingValidation, SetStockConfirmedOrderStatus ->
            let newState =
                { Buyer = awaitingValidation.Buyer
                  Address = awaitingValidation.Address
                  OrderDate = awaitingValidation.OrderDate
                  ConfirmedOrderItems =
                    awaitingValidation.UnconfirmedOrderItems
                    |> NonEmptyMap.mapValues OrderItemWithUnconfirmedStock.confirmStock }

            let event: DomainEvent.OrderStockConfirmed =
                { Buyer = newState.Buyer
                  ConfirmedOrderItems = newState.ConfirmedOrderItems }

            (newState |> T.WithConfirmedStock, [ event |> DomainEvent.OrderStockConfirmed ])
            |> Ok

        | WithConfirmedStock withConfirmedStock, SetPaidOrderStatus ->
            let newState =
                { Buyer = withConfirmedStock.Buyer
                  Address = withConfirmedStock.Address
                  OrderDate = withConfirmedStock.OrderDate
                  PaidOrderItems = withConfirmedStock.ConfirmedOrderItems }

            let event: DomainEvent.OrderPaid =
                { PaidBy = newState.Buyer
                  PaidOrderItems = newState.PaidOrderItems }

            (newState |> T.Paid, [ event |> DomainEvent.OrderPaid ]) |> Ok

        | Paid paid, ShipOrder ->
            let newState =
                { Buyer = paid.Buyer
                  Address = paid.Address
                  OrderDate = paid.OrderDate
                  ShippedOrderItems = paid.PaidOrderItems }

            let event: DomainEvent.OrderShipped =
                { ShippedTo = newState.Buyer
                  ShippedOrderItems = newState.ShippedOrderItems }

            (newState |> T.Shipped, [ event |> DomainEvent.OrderShipped ]) |> Ok

        | _, ShipOrder -> OnlyPaidOrderCanBeShipped |> Error

        | AwaitingStockValidation awaitingValidation, CancelOrder ->
            let newState =
                { Buyer = awaitingValidation.Buyer |> Buyer.WithVerifiedPaymentMethods
                  Address = awaitingValidation.Address
                  OrderDate = awaitingValidation.OrderDate
                  CancelledOrderItems =
                    awaitingValidation.UnconfirmedOrderItems
                    |> NonEmptyMap.mapValues OrderItem.WithUnconfirmedStock }

            let event: DomainEvent.OrderCancelled = { Buyer = newState.Buyer }

            (newState |> T.Cancelled, [ event |> DomainEvent.OrderCancelled ]) |> Ok

        | WithConfirmedStock withConfirmedStock, CancelOrder ->
            let newState =
                { Buyer = withConfirmedStock.Buyer |> Buyer.WithVerifiedPaymentMethods
                  Address = withConfirmedStock.Address
                  OrderDate = withConfirmedStock.OrderDate
                  CancelledOrderItems =
                    withConfirmedStock.ConfirmedOrderItems
                    |> NonEmptyMap.mapValues OrderItem.WithConfirmedStock }

            let event: DomainEvent.OrderCancelled = { Buyer = newState.Buyer }

            (newState |> T.Cancelled, [ event |> DomainEvent.OrderCancelled ]) |> Ok

        | Paid _, CancelOrder -> PaidOrderCannotBeCancelled |> Error

        | Shipped _, CancelOrder -> ShippedOrderCannotBeCancelled |> Error

        | state, _ -> (state, []) |> Ok

type Order = Order.T
