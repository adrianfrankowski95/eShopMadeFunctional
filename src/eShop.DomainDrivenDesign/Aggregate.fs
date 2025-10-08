namespace eShop.DomainDrivenDesign

open System
open eShop.ConstrainedTypes
open eShop.Prelude
open FsToolkit.ErrorHandling

[<Measure>]
type aggregate

type AggregateId<'state> = private AggregateId of Id<aggregate>

type GenerateAggregateId<'state> = unit -> AggregateId<'state>


[<RequireQualifiedAccess>]
module AggregateId =
    let ofGuid<'state> : Guid -> AggregateId<'state> = Id.ofGuid >> AggregateId

    let value (AggregateId rawId) = rawId |> Id.value

    let generate<'state> : GenerateAggregateId<'state> =
        fun () -> Id.generate<aggregate> () |> AggregateId

[<AutoOpen>]
module ActivePatterns =
    let (|AggregateId|) = AggregateId.value


[<RequireQualifiedAccess>]
module Aggregate =
    let typeName<'state> =
        let stateType = typeof<'state>

        stateType.DeclaringType.Name + stateType.Name


type AggregateAction<'st, 'ev, 'err, 'retn> =
    private | AggregateAction of ('st -> TaskResult<'st * 'ev list * 'retn, 'err>)

[<RequireQualifiedAccess>]
module AggregateAction =
    let private internalMap f g x =
        fun st -> x |> f (fun y -> st, [], y) |> g
        |> AggregateAction

    let internal run (AggregateAction a) st = a st

    let inline retn x =
        fun st0 -> TaskResult.ok (st0, [], x)
        |> AggregateAction

    let inline error x =
        fun _ -> TaskResult.error x
        |> AggregateAction

    let inline bind ([<InlineIfLambda>] f) a =
        fun st0 ->
            taskResult {
                let! st1, ev1, a = run a st0
                let! st2, ev2, b = run (f a) st1

                return st2, ev1 @ ev2, b
            }
        |> AggregateAction

    let inline combine a b = bind (fun _ -> b) a

    let inline map ([<InlineIfLambda>] f) a = bind (f >> retn) a

    let inline ignore a = map (fun _ -> ()) a

    let inline mapError ([<InlineIfLambda>] f) a =
        fun st0 -> run a st0 |> TaskResult.mapError f
        |> AggregateAction

    let inline apply f a = bind (fun f -> map f a) f

    let inline ofResult x =
        internalMap Result.map TaskResult.ofResult x

    let inline ofTaskResult x = internalMap TaskResult.map id x

    let inline exec ([<InlineIfLambda>] f) cmd =
        fun st0 -> f cmd st0 |> Result.map (fun (st, ev) -> st, ev, ()) |> TaskResult.ofResult
        |> AggregateAction

    let inline raise apply ev =
        fun st0 ->
            apply ev st0
            |> Result.map (fun (st, ev) -> st, [ ev ], ())
            |> TaskResult.ofResult
        |> AggregateAction

    let getState<'st, 'ev, 'err> : AggregateAction<'st, 'ev, 'err, 'st> =
        fun st -> TaskResult.ok (st, [], st)
        |> AggregateAction

    let inline private require req ([<InlineIfLambda>] f) err =
        getState |> bind (fun st -> f st |> req err |> ofResult)

    let inline requireSome ([<InlineIfLambda>] f) = require Result.requireSome f

    let inline requireNone ([<InlineIfLambda>] f) = require Result.requireNone f

    let inline requireTrue ([<InlineIfLambda>] f) = require Result.requireTrue f

    let inline requireFalse ([<InlineIfLambda>] f) = require Result.requireFalse f

type AggregateActionBuilder() =
    member _.Return(x) = AggregateAction.retn x
    member _.Bind(x, f) = AggregateAction.bind f x
    member _.ReturnFrom(x) = x
    member _.Zero() = AggregateAction.retn ()
    member _.Combine(x, y) = AggregateAction.combine x y
    member _.Delay(f) = f ()
    member _.Run(x) = x
    member _.Source(x: Result<_, _>) = x |> AggregateAction.ofResult
    member _.Source(x: TaskResult<_, _>) = x |> AggregateAction.ofTaskResult

type Workflow<'st, 'ev, 'err, 'ioErr, 'retn> =
    private | Workflow of AggregateAction<'st, 'ev, Either<'err, 'ioErr>, 'retn>

[<RequireQualifiedAccess>]
module Workflow =
    let inline private internalMapIoError f x =
        x |> TaskResult.mapError (Either.mapLeft f)

    let inline private internalMapDomainError f x =
        x |> TaskResult.mapError (Either.mapRight f)

    let internal run (Workflow a) st = AggregateAction.run a st

    let retn x = AggregateAction.retn x |> Workflow

    let inline ofAggregateAction a = a |> Workflow

    let usePort x =
        fun st0 -> x |> TaskResult.map (fun x -> st0, [], x) |> TaskResult.mapError Left
        |> AggregateAction
        |> Workflow

    let inline bind ([<InlineIfLambda>] f) a =
        fun st0 ->
            taskResult {
                let! st1, ev1, a = run a st0
                let! st2, ev2, b = run (f a) st1
            
                return st2, ev1 @ ev2, b
            }
        |> AggregateAction
        |> Workflow

    let inline combine a b = bind (fun _ -> b) a

    let inline map ([<InlineIfLambda>] f) a = bind (f >> retn) a

    let inline ignore a = map (fun _ -> ()) a

    let inline mapIoError ([<InlineIfLambda>] f) a =
        fun st0 -> run a st0 |> internalMapIoError f
        |> AggregateAction
        |> Workflow

    let inline mapDomainError ([<InlineIfLambda>] f) a =
        fun st0 -> run a st0 |> internalMapDomainError f
        |> AggregateAction
        |> Workflow

type WorkflowBuilder() =
    member _.Return(x) = Workflow.retn x
    member _.Bind(x, f) = Workflow.bind f x
    member _.ReturnFrom(x) = x
    member _.Zero() = Workflow.retn ()
    member _.Combine(x, y) = Workflow.combine x y
    member _.Delay(f) = f ()
    member _.Run(x) = x
    member _.Source(x: Workflow<_, _, _, _, _>) = x
    member _.Source(x: AggregateAction<_, _, _, _>) = x |> Workflow.ofAggregateAction

[<AutoOpen>]
module CE =
    let aggregateAction = AggregateActionBuilder()
    let workflow = WorkflowBuilder()
