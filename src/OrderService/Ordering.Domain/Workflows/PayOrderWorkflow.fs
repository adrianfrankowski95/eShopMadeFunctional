﻿namespace Ordering.Domain.Workflows

open eShop.DomainDrivenDesign
open Ordering.Domain.Model
open FsToolkit.ErrorHandling
open eShop.Prelude

[<RequireQualifiedAccess>]
module PayOrderWorkflow =
    type T<'ioError> = Workflow<unit, Order, DomainEvent, InvalidOrderStateError, 'ioError>

    let build: T<'ioError> =
        fun _ state _ ->
            Command.SetPaidOrderStatus
            |> Order.evolve state
            |> Result.mapError Left
            |> AsyncResult.ofResult

type PayOrderWorkflow<'ioError> = PayOrderWorkflow.T<'ioError>
