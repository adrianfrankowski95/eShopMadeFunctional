namespace eShop.DomainDrivenDesign

open System

type AggregateId<'state> = private AggregateId of Guid

[<RequireQualifiedAccess>]
module AggregateId =
    let create (rawId: string) =
        match Guid.TryParse(rawId) with
        | true, id -> id |> AggregateId |> Ok
        | false, _ -> rawId |> sprintf "Invalid AggregateId: %s" |> Error
    
    let ofGuid = AggregateId
    
    let value (AggregateId rawId) = rawId
    
    let toString (AggregateId rawId) = rawId.ToString()
    
[<AutoOpen>]
module ActivePatterns =
    let (|AggregateId|) = AggregateId.value