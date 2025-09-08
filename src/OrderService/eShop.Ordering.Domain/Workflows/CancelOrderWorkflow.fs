namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model

type CancelOrderWorkflow = AggregateAction<Order.State, Order.Event, Order.InvalidStateError>

[<RequireQualifiedAccess>]
module CancelOrderWorkflow =
    let build = Order.cancel
