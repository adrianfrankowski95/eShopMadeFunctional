namespace eShop.DomainDrivenDesign

open eShop.Prelude

type Workflow<'command, 'state, 'event, 'domainError> = MaybeAggregate<'state> -> 'command -> AsyncResult<'state * Event<'event> list, 'domainError>