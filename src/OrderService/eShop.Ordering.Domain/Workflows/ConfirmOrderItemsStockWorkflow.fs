namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module ConfirmOrderItemsStockWorkflow =
    type T<'ioError> = ExecutableWorkflow<unit, Order.State, Order.Event, Order.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            Order.Command.SetStockConfirmedOrderStatus
            |> Order.State.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type ConfirmOrderItemsStockWorkflow<'ioError> = ConfirmOrderItemsStockWorkflow.T<'ioError>
