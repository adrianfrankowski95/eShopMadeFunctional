namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type AwaitOrderStockItemsValidationWorkflow = AggregateAction<Order.State, Order.Event, Order.InvalidStateError>

[<RequireQualifiedAccess>]
module AwaitOrderStockItemsValidationWorkflow =
    let build: AwaitOrderStockItemsValidationWorkflow =
        Order.setAwaitingStockValidationStatus
