namespace Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module ConfirmOrderItemsStockWorkflow =
    type T<'ioError> = Workflow<unit, Order, DomainEvent, InvalidOrderStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            Command.SetStockConfirmedOrderStatus
            |> Order.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type ConfirmOrderItemsStockWorkflow<'ioError> = ConfirmOrderItemsStockWorkflow.T<'ioError>
