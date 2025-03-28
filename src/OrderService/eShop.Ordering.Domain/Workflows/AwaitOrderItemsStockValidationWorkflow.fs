namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module AwaitOrderStockItemsValidationWorkflow =
    type T<'ioError> = ExecutableWorkflow<unit, OrderAggregate.State, OrderAggregate.Event, OrderAggregate.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            OrderAggregate.Command.SetAwaitingStockValidationOrderStatus
            |> OrderAggregate.State.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type AwaitOrderStockItemsValidationWorkflow<'ioError> = ShipOrderWorkflow.T<'ioError>
