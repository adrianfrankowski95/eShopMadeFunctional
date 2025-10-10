namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type ShipOrderWorkflow<'ioErr> = OrderWorkflow<Order.InvalidStateError, 'ioErr, unit>

[<RequireQualifiedAccess>]
module ShipOrderWorkflow =
    let build<'ioErr> : ShipOrderWorkflow<'ioErr> =
        Order.setShippedStatus |> Workflow.ofAggregateAction
