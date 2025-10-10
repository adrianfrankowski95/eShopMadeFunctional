namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type CancelOrderWorkflow<'ioErr> = OrderWorkflow<Order.InvalidStateError, 'ioErr>

[<RequireQualifiedAccess>]
module CancelOrderWorkflow =
    let build<'ioErr> : CancelOrderWorkflow<'ioErr> =
        Order.cancel |> Workflow.ofAggregateAction
