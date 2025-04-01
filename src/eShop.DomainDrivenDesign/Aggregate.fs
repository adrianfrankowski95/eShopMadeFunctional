namespace eShop.DomainDrivenDesign

type AggregateId<'state> = private AggregateId of int

[<RequireQualifiedAccess>]
module AggregateId =
    let ofInt<'state> : int -> AggregateId<'state> = AggregateId

    let value (AggregateId rawId) = rawId

[<AutoOpen>]
module ActivePatterns =
    let (|AggregateId|) = AggregateId.value


[<RequireQualifiedAccess>]
module Aggregate =
    let typeName<'state> =
        let stateType = typeof<'state>

        stateType.DeclaringType.Name + stateType.Name
