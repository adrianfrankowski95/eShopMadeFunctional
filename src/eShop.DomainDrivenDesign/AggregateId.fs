namespace eShop.DomainDrivenDesign

type AggregateId<'state> = private AggregateId of int

[<RequireQualifiedAccess>]
module AggregateId =
    let ofInt = AggregateId

    let value (AggregateId rawId) = rawId

[<AutoOpen>]
module ActivePatterns =
    let (|AggregateId|) = AggregateId.value
