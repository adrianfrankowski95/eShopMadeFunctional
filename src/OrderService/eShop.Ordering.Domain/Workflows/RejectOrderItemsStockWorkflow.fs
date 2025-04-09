namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module RejectOrderItemsStockWorkflow =
    type T<'ioError> =
        ExecutableWorkflow<
            OrderAggregate.Command.SetStockRejectedOrderStatus,
            OrderAggregate.State,
            OrderAggregate.Event,
            OrderAggregate.InvalidStateError,
            'ioError
         >

    let build: T<'ioError> =
        fun _ state command ->
            command
            |> OrderAggregate.Command.SetStockRejectedOrderStatus
            |> OrderAggregate.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type RejectOrderItemsStockWorkflow<'ioError> = RejectOrderItemsStockWorkflow.T<'ioError>
