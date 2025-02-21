namespace Ordering.Domain.Model

open System
open Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes

type InvalidOrderStateError =
    | OnlyPaidOrderCanBeShipped
    | PaidOrderCannotBeCancelled
    | ShippedOrderCannotBeCancelled

[<RequireQualifiedAccess>]
module Order =
    type Draft =
        { BuyerId: BuyerId
          UnvalidatedOrderItems: Map<ProductId, UnvalidatedOrderItem> }

    type AwaitingStockValidation =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          OrderedAt: DateTimeOffset
          UnconfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithUnconfirmedStock> }

    type WithConfirmedStock =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          OrderedAt: DateTimeOffset
          ConfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Paid =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          OrderedAt: DateTimeOffset
          PaidOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Shipped =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          OrderedAt: DateTimeOffset
          ShippedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Cancelled =
        { Buyer: Buyer
          Address: Address
          OrderedAt: DateTimeOffset
          CancelledOrderItems: NonEmptyMap<ProductId, OrderItem> }

    type T =
        | Init
        | AwaitingStockValidation of AwaitingStockValidation
        | WithConfirmedStock of WithConfirmedStock
        | Paid of Paid
        | Shipped of Shipped
        | Cancelled of Cancelled

    let evolve (state: T) (command: Command) : Result<T * DomainEvent list, InvalidOrderStateError> =
        match state, command with
        | Init, CreateOrder cmd ->
            let newState =
                { Buyer = cmd.Buyer
                  Address = cmd.Address
                  PaymentMethod = cmd.PaymentMethod
                  OrderedAt = cmd.OrderedAt
                  UnconfirmedOrderItems = cmd.OrderItems }

            let events =
                [ ({ Buyer = newState.Buyer }: DomainEvent.OrderStarted)
                  |> DomainEvent.OrderStarted
                  ({ Buyer = newState.Buyer
                     VerifiedPaymentMethod = cmd.PaymentMethod }
                  : DomainEvent.PaymentMethodVerified)
                  |> DomainEvent.BuyerPaymentMethodVerified
                  ({ Buyer = newState.Buyer
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
                  PaymentMethod = awaitingValidation.PaymentMethod
                  OrderedAt = awaitingValidation.OrderedAt
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
                  PaymentMethod = withConfirmedStock.PaymentMethod
                  OrderedAt = withConfirmedStock.OrderedAt
                  PaidOrderItems = withConfirmedStock.ConfirmedOrderItems }

            let event: DomainEvent.OrderPaid =
                { PaidBy = newState.Buyer
                  PaidOrderItems = newState.PaidOrderItems }

            (newState |> T.Paid, [ event |> DomainEvent.OrderPaid ]) |> Ok

        | Paid paid, ShipOrder ->
            let newState =
                { Buyer = paid.Buyer
                  Address = paid.Address
                  PaymentMethod = paid.PaymentMethod
                  OrderedAt = paid.OrderedAt
                  ShippedOrderItems = paid.PaidOrderItems }

            let event: DomainEvent.OrderShipped =
                { ShippedTo = newState.Buyer
                  ShippedOrderItems = newState.ShippedOrderItems }

            (newState |> T.Shipped, [ event |> DomainEvent.OrderShipped ]) |> Ok

        | _, ShipOrder -> OnlyPaidOrderCanBeShipped |> Error

        | AwaitingStockValidation awaitingValidation, CancelOrder ->
            let newState =
                { Buyer = awaitingValidation.Buyer
                  Address = awaitingValidation.Address
                  OrderedAt = awaitingValidation.OrderedAt
                  CancelledOrderItems =
                    awaitingValidation.UnconfirmedOrderItems
                    |> NonEmptyMap.mapValues OrderItem.WithUnconfirmedStock }

            let event: DomainEvent.OrderCancelled = { Buyer = newState.Buyer }

            (newState |> T.Cancelled, [ event |> DomainEvent.OrderCancelled ]) |> Ok

        | WithConfirmedStock withConfirmedStock, CancelOrder ->
            let newState =
                { Buyer = withConfirmedStock.Buyer
                  Address = withConfirmedStock.Address
                  OrderedAt = withConfirmedStock.OrderedAt
                  CancelledOrderItems =
                    withConfirmedStock.ConfirmedOrderItems
                    |> NonEmptyMap.mapValues OrderItem.WithConfirmedStock }

            let event: DomainEvent.OrderCancelled = { Buyer = newState.Buyer }

            (newState |> T.Cancelled, [ event |> DomainEvent.OrderCancelled ]) |> Ok

        | Paid _, CancelOrder -> PaidOrderCannotBeCancelled |> Error

        | Shipped _, CancelOrder -> ShippedOrderCannotBeCancelled |> Error

        | state, _ -> (state, []) |> Ok

type Order = Order.T
