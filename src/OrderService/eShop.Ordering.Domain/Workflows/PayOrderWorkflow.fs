namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type PayOrderWorkflow = AggregateAction<Order.State, Order.Event, Order.InvalidStateError>

[<RequireQualifiedAccess>]
module PayOrderWorkflow =
    let build: PayOrderWorkflow = Order.setPaidStatus
