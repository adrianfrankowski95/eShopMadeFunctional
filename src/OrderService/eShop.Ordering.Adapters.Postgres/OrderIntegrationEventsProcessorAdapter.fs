[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderIntegrationEventsProcessorAdapter

open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres

type PersistOrderIntegrationEvent =
    PersistEvent<OrderAggregate.State, Postgres.EventId, IntegrationEvent.Consumed, SqlIoError>

let persistOrderIntegrationEvent dbSchema sqlSession : PersistOrderIntegrationEvent =
    Postgres.persistEvent id dbSchema sqlSession

type ReadUnprocessedOrderIntegrationEvents =
    ReadUnprocessedEvents<OrderAggregate.State, Postgres.EventId, IntegrationEvent.Consumed, SqlIoError>

let readUnprocessedOrderIntegrationEvents dbSchema sqlSession : ReadUnprocessedOrderIntegrationEvents =
    Postgres.readUnprocessedEvents id dbSchema sqlSession
