namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module CancelOrderWorkflow =
    type T<'ioError> = Workflow<unit, Order.State, Order.Event, Order.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            Order.Command.CancelOrder
            |> Order.State.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type CancelOrderWorkflow<'ioError> = CancelOrderWorkflow.T<'ioError>
