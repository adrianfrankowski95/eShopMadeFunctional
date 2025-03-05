namespace eShop.Ordering.Domain.Model

open System
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes
open eShop.Prelude

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
                  |> DomainEvent.PaymentMethodVerified
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
                { Buyer = newState.Buyer
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
                { Buyer = newState.Buyer
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

    let getBuyerId =
        function
        | Init -> None
        | Draft draft -> draft.BuyerId |> Some
        | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.Buyer.Id |> Some
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Buyer.Id |> Some
        | Paid paid -> paid.Buyer.Id |> Some
        | Shipped shipped -> shipped.Buyer.Id |> Some
        | Cancelled cancelled -> cancelled.Buyer.Id |> Some

    let getBuyerName =
        function
        | Init -> None
        | Draft _ -> None
        | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.Buyer.Name |> Some
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Buyer.Name |> Some
        | Paid paid -> paid.Buyer.Name |> Some
        | Shipped shipped -> shipped.Buyer.Name |> Some
        | Cancelled cancelled -> cancelled.Buyer.Name |> Some

    let getAddress =
        function
        | Init -> None
        | Draft _ -> None
        | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.Address |> Some
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Address |> Some
        | Paid paid -> paid.Address |> Some
        | Shipped shipped -> shipped.Address |> Some
        | Cancelled cancelled -> cancelled.Address |> Some

    let getStartedAt =
        function
        | Init -> None
        | Draft _ -> None
        | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.StartedAt |> Some
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.StartedAt |> Some
        | Paid paid -> paid.StartedAt |> Some
        | Shipped shipped -> shipped.StartedAt |> Some
        | Cancelled cancelled -> cancelled.StartedAt |> Some

    let getOrderItems =
        function
        | Init -> Map.empty

        | Draft draft -> draft.UnvalidatedOrderItems |> Map.mapValues OrderItem.Unvalidated

        | AwaitingStockValidation awaitingStockValidation ->
            awaitingStockValidation.UnconfirmedOrderItems
            |> NonEmptyMap.mapValues OrderItem.WithUnconfirmedStock
            |> NonEmptyMap.toMap

        | WithConfirmedStock withConfirmedStock ->
            withConfirmedStock.ConfirmedOrderItems
            |> NonEmptyMap.mapValues OrderItem.WithConfirmedStock
            |> NonEmptyMap.toMap

        | Paid paid ->
            paid.PaidOrderItems
            |> NonEmptyMap.mapValues OrderItem.WithConfirmedStock
            |> NonEmptyMap.toMap

        | Shipped shipped ->
            shipped.ShippedOrderItems
            |> NonEmptyMap.mapValues OrderItem.WithConfirmedStock
            |> NonEmptyMap.toMap

        | Cancelled cancelled ->
            cancelled.CancelledOrderItems
            |> NonEmptyMap.mapValues OrderItem.Unvalidated
            |> NonEmptyMap.toMap

    let getPaymentMethod =
        function
        | Init -> None
        | Draft _ -> None
        | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.PaymentMethod |> Some
        | WithConfirmedStock withConfirmedStock -> withConfirmedStock.PaymentMethod |> Some
        | Paid paid -> paid.PaymentMethod |> Some
        | Shipped shipped -> shipped.PaymentMethod |> Some
        | Cancelled _ -> None

type Order = Order.T
