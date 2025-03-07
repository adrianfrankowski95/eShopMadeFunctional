﻿namespace eShop.Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module CancelOrderWorkflow =
    type T<'ioError> = Workflow<unit, Order, DomainEvent, InvalidOrderStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            Command.CancelOrder
            |> Order.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type CancelOrderWorkflow<'ioError> = CancelOrderWorkflow.T<'ioError>
