namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type ShipOrderWorkflow<'ioErr> = OrderWorkflow<Order.InvalidStateError, 'ioErr>

[<RequireQualifiedAccess>]
module ShipOrderWorkflow =
    let build<'ioErr> : ShipOrderWorkflow<'ioErr> =
        Order.setShippedStatus |> Workflow.ofAggregateAction
