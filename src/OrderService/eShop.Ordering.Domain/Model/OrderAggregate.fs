[<RequireQualifiedAccess>]
module eShop.Ordering.Domain.Model.OrderAggregate

open System
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.ConstrainedTypes
open eShop.Prelude
open FsToolkit.ErrorHandling

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

    type T =
        | CreateOrderDraft of CreateOrderDraft
        | CreateOrder of CreateOrder
        | SetAwaitingStockValidationOrderStatus
        | SetStockConfirmedOrderStatus
        | SetStockRejectedOrderStatus of SetStockRejectedOrderStatus
        | SetPaidOrderStatus
        | SetShippedOrderStatus
        | CancelOrder

type Command = Command.T

[<RequireQualifiedAccess>]
module Event =
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

    type T =
        internal
        | OrderStarted of OrderStarted
        | PaymentMethodVerified of PaymentMethodVerified
        | OrderCancelled of OrderCancelled
        | OrderStatusChangedToAwaitingValidation of OrderStatusChangedToAwaitingValidation
        | OrderStockConfirmed of OrderStockConfirmed
        | OrderPaid of OrderPaid
        | OrderShipped of OrderShipped

type Event = Event.T

type InvalidStateError =
    | OnlyPaidOrderCanBeShipped
    | PaidOrderCannotBeCancelled
    | ShippedOrderCannotBeCancelled

[<RequireQualifiedAccess>]
module State =
    type Draft =
        { BuyerId: BuyerId
          UnvalidatedOrderItems: Map<ProductId, UnvalidatedOrderItem> }

    type Submitted =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          StartedAt: DateTimeOffset
          UnconfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithUnconfirmedStock> }

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
          ConfirmedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock>
          Description: Description }

    type Paid =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          StartedAt: DateTimeOffset
          PaidOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock>
          Description: Description }

    type Shipped =
        { Buyer: Buyer
          PaymentMethod: VerifiedPaymentMethod
          Address: Address
          StartedAt: DateTimeOffset
          ShippedOrderItems: NonEmptyMap<ProductId, OrderItemWithConfirmedStock>
          Description: Description }

    type Cancelled =
        { Buyer: Buyer
          Address: Address
          StartedAt: DateTimeOffset
          CancelledOrderItems: NonEmptyMap<ProductId, UnvalidatedOrderItem>
          Description: Description }

type State =
    internal
    | Init
    | Draft of State.Draft
    | Submitted of State.Submitted
    | AwaitingStockValidation of State.AwaitingStockValidation
    | WithConfirmedStock of State.WithConfirmedStock
    | Paid of State.Paid
    | Shipped of State.Shipped
    | Cancelled of State.Cancelled

let evolve (state: State) (command: Command) : Result<State * Event list, InvalidStateError> =
    let forceNonWhiteSpaceDescription rawValue =
        rawValue |> Description.create |> Result.valueOr failwith

    match state, command with
    | Init, Command.CreateOrder cmd ->
        let newState: State.Submitted =
            { Buyer = cmd.Buyer
              Address = cmd.Address
              PaymentMethod = cmd.PaymentMethod
              StartedAt = cmd.OrderedAt
              UnconfirmedOrderItems = cmd.OrderItems }

        let events =
            [ ({ Buyer = newState.Buyer }: Event.OrderStarted) |> Event.OrderStarted
              ({ Buyer = newState.Buyer
                 VerifiedPaymentMethod = cmd.PaymentMethod }
              : Event.PaymentMethodVerified)
              |> Event.PaymentMethodVerified ]

        (newState |> State.Submitted, events) |> Ok

    | Submitted submitted, Command.SetAwaitingStockValidationOrderStatus ->
        let newState: State.AwaitingStockValidation =
            { Buyer = submitted.Buyer
              Address = submitted.Address
              PaymentMethod = submitted.PaymentMethod
              StartedAt = submitted.StartedAt
              UnconfirmedOrderItems = submitted.UnconfirmedOrderItems }

        let events =
            [ ({ Buyer = newState.Buyer
                 StockToValidate =
                   newState.UnconfirmedOrderItems
                   |> NonEmptyMap.map (fun _ orderItem -> orderItem.Units) }
              : Event.OrderStatusChangedToAwaitingValidation)
              |> Event.OrderStatusChangedToAwaitingValidation ]

        (newState |> State.AwaitingStockValidation, events) |> Ok

    | AwaitingStockValidation awaitingValidation, Command.SetStockConfirmedOrderStatus ->
        let newState: State.WithConfirmedStock =
            { Buyer = awaitingValidation.Buyer
              Address = awaitingValidation.Address
              PaymentMethod = awaitingValidation.PaymentMethod
              StartedAt = awaitingValidation.StartedAt
              ConfirmedOrderItems =
                awaitingValidation.UnconfirmedOrderItems
                |> NonEmptyMap.mapValues OrderItemWithUnconfirmedStock.confirmStock
              Description =
                "All the items were confirmed with available stock."
                |> forceNonWhiteSpaceDescription }

        let event: Event.OrderStockConfirmed =
            { Buyer = newState.Buyer
              ConfirmedOrderItems = newState.ConfirmedOrderItems }

        (newState |> State.WithConfirmedStock, [ event |> Event.OrderStockConfirmed ])
        |> Ok

    | AwaitingStockValidation awaitingValidation, Command.SetStockRejectedOrderStatus cmd ->
        let newState: State.Cancelled =
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
                      UnitPrice = item.UnitPrice })
              Description =
                awaitingValidation.UnconfirmedOrderItems
                |> NonEmptyMap.filter (fun k _ -> cmd.RejectedOrderItems |> NonEmptyList.contains k)
                |> NonEmptyMap.values
                |> Seq.map (_.ProductName >> ProductName.value)
                |> String.concat ", "
                |> sprintf "The product items don't have stock: %s"
                |> forceNonWhiteSpaceDescription }

        (newState |> State.Cancelled, []) |> Ok

    | WithConfirmedStock withConfirmedStock, Command.SetPaidOrderStatus ->
        let newState: State.Paid =
            { Buyer = withConfirmedStock.Buyer
              Address = withConfirmedStock.Address
              PaymentMethod = withConfirmedStock.PaymentMethod
              StartedAt = withConfirmedStock.StartedAt
              PaidOrderItems = withConfirmedStock.ConfirmedOrderItems
              Description =
                "The payment was performed at a simulated \"American Bank checking bank account ending on XX35071\""
                |> forceNonWhiteSpaceDescription }

        let event: Event.OrderPaid =
            { Buyer = newState.Buyer
              PaidOrderItems = newState.PaidOrderItems }

        (newState |> State.Paid, [ event |> Event.OrderPaid ]) |> Ok

    | Paid paid, Command.SetShippedOrderStatus ->
        let newState: State.Shipped =
            { Buyer = paid.Buyer
              Address = paid.Address
              PaymentMethod = paid.PaymentMethod
              StartedAt = paid.StartedAt
              ShippedOrderItems = paid.PaidOrderItems
              Description = "The order was shipped." |> forceNonWhiteSpaceDescription }

        let event: Event.OrderShipped =
            { Buyer = newState.Buyer
              ShippedOrderItems = newState.ShippedOrderItems }

        (newState |> State.Shipped, [ event |> Event.OrderShipped ]) |> Ok

    | _, Command.SetShippedOrderStatus -> OnlyPaidOrderCanBeShipped |> Error

    | Submitted submitted, Command.CancelOrder ->
        let newState: State.Cancelled =
            { Buyer = submitted.Buyer
              Address = submitted.Address
              StartedAt = submitted.StartedAt
              CancelledOrderItems =
                submitted.UnconfirmedOrderItems
                |> NonEmptyMap.mapValues (fun item ->
                    { Discount = item.Discount
                      Units = item.Units
                      PictureUrl = item.PictureUrl
                      ProductName = item.ProductName
                      UnitPrice = item.UnitPrice })
              Description = "The order was cancelled." |> forceNonWhiteSpaceDescription }

        let event: Event.OrderCancelled = { Buyer = newState.Buyer }

        (newState |> State.Cancelled, [ event |> Event.OrderCancelled ]) |> Ok

    | AwaitingStockValidation awaitingValidation, Command.CancelOrder ->
        let newState: State.Cancelled =
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
                      UnitPrice = item.UnitPrice })
              Description = "The order was cancelled." |> forceNonWhiteSpaceDescription }

        let event: Event.OrderCancelled = { Buyer = newState.Buyer }

        (newState |> State.Cancelled, [ event |> Event.OrderCancelled ]) |> Ok

    | WithConfirmedStock withConfirmedStock, Command.CancelOrder ->
        let newState: State.Cancelled =
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
                      UnitPrice = item.UnitPrice })
              Description = "The order was cancelled." |> forceNonWhiteSpaceDescription }

        let event: Event.OrderCancelled = { Buyer = newState.Buyer }

        (newState |> State.Cancelled, [ event |> Event.OrderCancelled ]) |> Ok

    | Paid _, Command.CancelOrder -> PaidOrderCannotBeCancelled |> Error

    | Shipped _, Command.CancelOrder -> ShippedOrderCannotBeCancelled |> Error

    | state, _ -> (state, []) |> Ok

let getBuyerId =
    function
    | Init -> None
    | Draft draft -> draft.BuyerId |> Some
    | Submitted submitted -> submitted.Buyer.Id |> Some
    | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.Buyer.Id |> Some
    | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Buyer.Id |> Some
    | Paid paid -> paid.Buyer.Id |> Some
    | Shipped shipped -> shipped.Buyer.Id |> Some
    | Cancelled cancelled -> cancelled.Buyer.Id |> Some

let getBuyerName =
    function
    | Init -> None
    | Draft _ -> None
    | Submitted submitted -> submitted.Buyer.Name |> Some
    | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.Buyer.Name |> Some
    | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Buyer.Name |> Some
    | Paid paid -> paid.Buyer.Name |> Some
    | Shipped shipped -> shipped.Buyer.Name |> Some
    | Cancelled cancelled -> cancelled.Buyer.Name |> Some

let getAddress =
    function
    | Init -> None
    | Draft _ -> None
    | Submitted submitted -> submitted.Address |> Some
    | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.Address |> Some
    | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Address |> Some
    | Paid paid -> paid.Address |> Some
    | Shipped shipped -> shipped.Address |> Some
    | Cancelled cancelled -> cancelled.Address |> Some

let getDescription =
    function
    | Init -> None
    | Draft _ -> None
    | Submitted _ -> None
    | AwaitingStockValidation _ -> None
    | WithConfirmedStock withConfirmedStock -> withConfirmedStock.Description |> Some
    | Paid paid -> paid.Description |> Some
    | Shipped shipped -> shipped.Description |> Some
    | Cancelled cancelled -> cancelled.Description |> Some

let getStartedAt =
    function
    | Init -> None
    | Draft _ -> None
    | Submitted submitted -> submitted.StartedAt |> Some
    | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.StartedAt |> Some
    | WithConfirmedStock withConfirmedStock -> withConfirmedStock.StartedAt |> Some
    | Paid paid -> paid.StartedAt |> Some
    | Shipped shipped -> shipped.StartedAt |> Some
    | Cancelled cancelled -> cancelled.StartedAt |> Some

let getOrderItems =
    function
    | Init -> Map.empty

    | Draft draft -> draft.UnvalidatedOrderItems |> Map.mapValues OrderItem.Unvalidated

    | Submitted submitted ->
        submitted.UnconfirmedOrderItems
        |> NonEmptyMap.mapValues OrderItem.WithUnconfirmedStock
        |> NonEmptyMap.toMap

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
    | Submitted submitted -> submitted.PaymentMethod |> Some
    | AwaitingStockValidation awaitingStockValidation -> awaitingStockValidation.PaymentMethod |> Some
    | WithConfirmedStock withConfirmedStock -> withConfirmedStock.PaymentMethod |> Some
    | Paid paid -> paid.PaymentMethod |> Some
    | Shipped shipped -> shipped.PaymentMethod |> Some
    | Cancelled _ -> None
