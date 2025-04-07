[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderIntegrationEventsProcessorAdapter

open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres

type PersistOrderIntegrationEvents = PersistEvents<OrderAggregate.State, IntegrationEvent.Consumed, SqlIoError>

let persistOrderIntegrationEvents dbSchema sqlSession : PersistOrderIntegrationEvents =
    Postgres.persistEvents id dbSchema sqlSession

type ReadUnprocessedOrderIntegrationEvents =
    ReadUnprocessedEvents<OrderAggregate.State, IntegrationEvent.Consumed, SqlIoError>

let readUnprocessedOrderIntegrationEvents dbSchema sqlSession : ReadUnprocessedOrderIntegrationEvents =
    Postgres.readUnprocessedEvents id dbSchema sqlSession
