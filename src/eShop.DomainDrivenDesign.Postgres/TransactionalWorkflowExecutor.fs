[<RequireQualifiedAccess>]
module eShop.DomainDrivenDesign.Postgres

open System.Data
open eShop.DomainDrivenDesign

type WorkflowInTransaction<'command, 'state, 'event, 'domainError, 'ioError> =
    IDbTransaction -> Workflow<'command, 'state, 'event, 'domainError, 'ioError>

[<RequireQualifiedAccess>]
module TransactionalWorkflowExecutor =