namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module PayOrderWorkflow =
    type T<'ioError> = ExecutableWorkflow<unit, OrderAggregate.State, OrderAggregate.Event, OrderAggregate.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            OrderAggregate.Command.SetPaidOrderStatus
            |> OrderAggregate.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type PayOrderWorkflow<'ioError> = PayOrderWorkflow.T<'ioError>
