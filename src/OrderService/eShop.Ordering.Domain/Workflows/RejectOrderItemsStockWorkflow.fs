namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type RejectOrderItemsStockWorkflow<'ioErr> =
    Order.Command.SetStockRejectedOrderStatus -> OrderWorkflow<Order.InvalidStateError, 'ioErr, unit>

[<RequireQualifiedAccess>]
module RejectOrderItemsStockWorkflow =
    let build: RejectOrderItemsStockWorkflow<'ioError> =
        fun command -> command |> Order.setStockRejectedStatus |> Workflow.ofAggregateAction
