namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type PayOrderWorkflow<'ioErr> = OrderWorkflow<Order.InvalidStateError, 'ioErr>

[<RequireQualifiedAccess>]
module PayOrderWorkflow =
    let build<'ioErr> : PayOrderWorkflow<'ioErr> =
        Order.setPaidStatus |> Workflow.ofAggregateAction
