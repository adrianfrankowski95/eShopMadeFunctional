namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module ConfirmOrderItemsStockWorkflow =
    type T<'ioError> = ExecutableWorkflow<unit, OrderAggregate.State, OrderAggregate.Event, OrderAggregate.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            OrderAggregate.Command.SetStockConfirmedOrderStatus
            |> OrderAggregate.State.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type ConfirmOrderItemsStockWorkflow<'ioError> = ConfirmOrderItemsStockWorkflow.T<'ioError>
