namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

type ShipOrderWorkflow = AggregateAction<Order.State, Order.Event, Order.InvalidStateError>

[<RequireQualifiedAccess>]
module ShipOrderWorkflow =
    let build: ShipOrderWorkflow = Order.setShippedStatus
