namespace eShop.Ordering.Domain.Model

open System
open eShop.Ordering.Domain.Model.ValueObjects
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
          StartedAt: DateTimeOffset
          UnconfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithUnconfirmedStock> }

    type WithConfirmedStock =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          StartedAt: DateTimeOffset
          ConfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Paid =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          StartedAt: DateTimeOffset
          PaidOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Shipped =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          StartedAt: DateTimeOffset
          ShippedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock> }

    type Cancelled =
        { Buyer: Buyer
          Address: Address
          StartedAt: DateTimeOffset
          CancelledOrderItems: NonEmptyMap<ProductId, UnvalidatedOrderItem> }

    type T =
        internal
        | Init
        | Draft of Draft
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
                  StartedAt = cmd.OrderedAt
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
                  StartedAt = awaitingValidation.StartedAt
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
                  StartedAt = withConfirmedStock.StartedAt
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
                  StartedAt = paid.StartedAt
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
                  StartedAt = awaitingValidation.StartedAt
                  CancelledOrderItems =
                    awaitingValidation.UnconfirmedOrderItems
                    |> NonEmptyMap.mapValues (fun item ->
                        { Discount = item.Discount
                          Units = item.Units
                          PictureUrl = item.PictureUrl
                          ProductName = item.ProductName
                          UnitPrice = item.UnitPrice }) }

            let event: DomainEvent.OrderCancelled = { Buyer = newState.Buyer }

            (newState |> T.Cancelled, [ event |> DomainEvent.OrderCancelled ]) |> Ok

        | WithConfirmedStock withConfirmedStock, CancelOrder ->
            let newState =
                { Buyer = withConfirmedStock.Buyer
                  Address = withConfirmedStock.Address
                  StartedAt = withConfirmedStock.StartedAt
                  CancelledOrderItems =
                    withConfirmedStock.ConfirmedOrderItems
                    |> NonEmptyMap.mapValues (fun item ->
                        { Discount = item.Discount
                          Units = item.Units
                          PictureUrl = item.PictureUrl
                          ProductName = item.ProductName
                          UnitPrice = item.UnitPrice }) }

            let event: DomainEvent.OrderCancelled = { Buyer = newState.Buyer }

            (newState |> T.Cancelled, [ event |> DomainEvent.OrderCancelled ]) |> Ok

        | Paid _, CancelOrder -> PaidOrderCannotBeCancelled |> Error

        | Shipped _, CancelOrder -> ShippedOrderCannotBeCancelled |> Error

        | state, _ -> (state, []) |> Ok

type Order = Order.T
