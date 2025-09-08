namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type ConfirmOrderItemsStockWorkflow = AggregateAction<Order.State, Order.Event, Order.InvalidStateError>

[<RequireQualifiedAccess>]
module ConfirmOrderItemsStockWorkflow =
    let build: ConfirmOrderItemsStockWorkflow = Order.setStockConfirmedStatus
