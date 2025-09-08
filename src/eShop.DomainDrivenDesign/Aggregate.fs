namespace eShop.DomainDrivenDesign

open System
open eShop.ConstrainedTypes

[<Measure>]
type aggregateId

type AggregateId<'state> = private AggregateId of Id<aggregateId>

type GenerateAggregateId<'state> = unit -> AggregateId<'state>

[<RequireQualifiedAccess>]
module AggregateId =
    let ofGuid<'state> : Guid -> AggregateId<'state> = Id.ofGuid >> AggregateId

    let value (AggregateId rawId) = rawId |> Id.value

    let generate<'state> : GenerateAggregateId<'state> =
        fun () -> Id.generate<aggregateId> () |> AggregateId

[<AutoOpen>]
module ActivePatterns =
    let (|AggregateId|) = AggregateId.value

type Evolve<'state, 'command, 'event, 'stateTransitionError> =
    'command -> 'state -> Result<'state * 'event list, 'stateTransitionError>

type AggregateAction<'state, 'event, 'stateTransitionError> =
    'state -> Result<'state * 'event list, 'stateTransitionError>

[<RequireQualifiedAccess>]
module Aggregate =
    let typeName<'state> =
        let stateType = typeof<'state>

        stateType.DeclaringType.Name + stateType.Name
