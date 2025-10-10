namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type ConfirmOrderItemsStockWorkflow<'ioErr> = OrderWorkflow<Order.InvalidStateError, 'ioErr>

[<RequireQualifiedAccess>]
module ConfirmOrderItemsStockWorkflow =
    let build<'ioErr> : ConfirmOrderItemsStockWorkflow<'ioErr> =
        Order.setStockConfirmedStatus |> Workflow.ofAggregateAction
