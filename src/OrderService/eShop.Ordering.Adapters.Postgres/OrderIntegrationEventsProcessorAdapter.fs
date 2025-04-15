[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderIntegrationEventsProcessorAdapter

open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres

type PersistOrderIntegrationEvents = PersistEvents<OrderAggregate.State, IntegrationEvent.Consumed, SqlIoError>

let persistOrderIntegrationEvents jsonOptions dbSchema sqlSession : PersistOrderIntegrationEvents =
    Postgres.persistEvents id jsonOptions dbSchema sqlSession

type ReadUnprocessedOrderIntegrationEvents =
    ReadUnprocessedEvents<OrderAggregate.State, IntegrationEvent.Consumed, SqlIoError>

let readUnprocessedOrderIntegrationEvents jsonOptions dbSchema sqlSession : ReadUnprocessedOrderIntegrationEvents =
    Postgres.readUnprocessedEvents Ok jsonOptions dbSchema sqlSession
