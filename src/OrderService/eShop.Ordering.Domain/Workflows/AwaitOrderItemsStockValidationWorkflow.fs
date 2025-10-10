namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type AwaitOrderStockItemsValidationWorkflow<'ioErr> = OrderWorkflow<Order.InvalidStateError, 'ioErr, unit>

[<RequireQualifiedAccess>]
module AwaitOrderStockItemsValidationWorkflow =
    let build<'ioErr> : AwaitOrderStockItemsValidationWorkflow<'ioErr> =
        Order.setAwaitingStockValidationStatus |> Workflow.ofAggregateAction
