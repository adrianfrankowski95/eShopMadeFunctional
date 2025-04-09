namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module ShipOrderWorkflow =
    type T<'ioError> = ExecutableWorkflow<unit, OrderAggregate.State, OrderAggregate.Event, OrderAggregate.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            OrderAggregate.Command.SetShippedOrderStatus
            |> OrderAggregate.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type ShipOrderWorkflow<'ioError> = ShipOrderWorkflow.T<'ioError>
