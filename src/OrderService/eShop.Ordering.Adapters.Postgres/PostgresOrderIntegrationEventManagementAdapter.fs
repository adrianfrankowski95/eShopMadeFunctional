[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.PostgresOrderIntegrationEventManagementAdapter

open eShop.Ordering.Domain.Model
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres
open eShop.Ordering.Adapters.Common

type PersistOrderIntegrationEvents = PersistEvents<OrderAggregate.State, Postgres.EventId, IntegrationEvent.Consumed, SqlIoError>

let persistOrderIntegrationEvents dbSchema dbTransaction : PersistOrderIntegrationEvents =
    Postgres.persistEvents id dbSchema dbTransaction

type ReadUnprocessedOrderIntegrationEvents =
    ReadUnprocessedEvents<OrderAggregate.State, Postgres.EventId, IntegrationEvent.Consumed, SqlIoError>

let readUnprocessedOrderIntegrationEvents dbSchema sqlSession : ReadUnprocessedOrderIntegrationEvents =
    Postgres.readUnprocessedEvents id dbSchema sqlSession

type OrderIntegrationEventsProcessor<'eventHandlerIoError> =
    EventsProcessor<OrderAggregate.State, Postgres.EventId, IntegrationEvent.Consumed, SqlIoError, 'eventHandlerIoError>
