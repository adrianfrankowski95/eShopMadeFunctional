[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.OrderIntegrationEventsAdapter

open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres

type PersistOrderIntegrationEvents = PersistEvents<Order.State, IntegrationEvent.Consumed, SqlIoError>

let persistOrderIntegrationEvents jsonOptions dbSchema dbTransaction : PersistOrderIntegrationEvents =
    Postgres.persistEvents id jsonOptions dbSchema dbTransaction

type ReadUnprocessedOrderIntegrationEvents =
    ReadUnprocessedEvents<Order.State, IntegrationEvent.Consumed, SqlIoError>

let readUnprocessedOrderIntegrationEvents jsonOptions dbSchema sqlSession : ReadUnprocessedOrderIntegrationEvents =
    Postgres.readUnprocessedEvents Ok jsonOptions dbSchema sqlSession
