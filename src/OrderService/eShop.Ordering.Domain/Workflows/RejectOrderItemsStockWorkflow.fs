﻿namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module RejectOrderItemsStockWorkflow =
    type T<'ioError> =
        Workflow<Order.Command.SetStockRejectedOrderStatus, Order.State, Order.Event, Order.InvalidStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state command ->
            command
            |> Order.Command.SetStockRejectedOrderStatus
            |> Order.State.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type RejectOrderItemsStockWorkflow<'ioError> = RejectOrderItemsStockWorkflow.T<'ioError>
