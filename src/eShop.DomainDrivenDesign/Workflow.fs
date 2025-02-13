namespace eShop.DomainDrivenDesign

open System
open eShop.Prelude

type UtcNow = DateTimeOffset

type Workflow<'command, 'state, 'event, 'domainError, 'ioError> =
    UtcNow -> 'state -> 'command -> AsyncResult<'state * 'event list, Either<'domainError, 'ioError>>
