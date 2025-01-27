namespace eShop.DomainDrivenDesign

type Aggregate<'state> = AggregateId<'state> * 'state

type AggregateAlreadyExistsError<'state> = AggregateAlreadyExistsError of AggregateId<'state>
type AggregateDoesNotExistError<'state> = AggregateDoesNotExistError of AggregateId<'state>

type MaybeAggregate<'state> =
    | New of AggregateId<'state>
    | Existing of Aggregate<'state>
    
module MaybeAggregate =
    let ofOption aggregateId =
        function
        | Some aggregate -> Existing aggregate
        | None -> New aggregateId
    
    let requireExisting =
        function
        | Existing (_, state) -> state |> Ok
        | New id -> id |> AggregateDoesNotExistError |> Error
        
    let requireNew =
        function
        | New id -> id |> Ok
        | Existing (id, _) -> id |> AggregateAlreadyExistsError |> Error