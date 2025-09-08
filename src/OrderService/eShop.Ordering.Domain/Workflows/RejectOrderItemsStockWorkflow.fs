namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

type RejectOrderItemsStockWorkflow<'ioError> =
    Workflow<Order.Command.SetStockRejectedOrderStatus, Order.State, Order.Event, Order.InvalidStateError, 'ioError>

[<RequireQualifiedAccess>]
module RejectOrderItemsStockWorkflow =
    let build: RejectOrderItemsStockWorkflow<'ioError> =
        fun _ state command ->
            state
            |> Order.setStockRejectedStatus command
            |> Result.mapError Left
            |> AsyncResult.ofResult
