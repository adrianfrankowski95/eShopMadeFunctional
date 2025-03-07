﻿namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module RejectOrderItemsStockWorkflow =
    type T<'ioError> = Workflow<Command.SetStockRejectedOrderStatus, Order, DomainEvent, InvalidOrderStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state command ->
            command
            |> Command.SetStockRejectedOrderStatus
            |> Order.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type RejectOrderItemsStockWorkflow<'ioError> = RejectOrderItemsStockWorkflow.T<'ioError>
