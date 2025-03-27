[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.PostgresOrderIntegrationEventsManagementAdapter

open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Postgres

type PersistOrderIntegrationEvents<'eventPayload> =
    PersistEvents<Order.State, Postgres.EventId, 'eventPayload, SqlIoError>

let persistOrderIntegrationEvents dbSchema dbTransaction : PersistOrderIntegrationEvents<_> =
    Postgres.persistEvents id dbSchema dbTransaction

type ReadUnprocessedOrderIntegrationEvents<'eventPayload> =
    ReadUnprocessedEvents<Order.State, Postgres.EventId, 'eventPayload, SqlIoError>

let readUnprocessedOrderIntegrationEvents dbSchema sqlSession : ReadUnprocessedOrderIntegrationEvents<_> =
    Postgres.readUnprocessedEvents id dbSchema sqlSession

type OrderIntegrationEventsProcessor<'eventPayload, 'eventHandlerIoError> =
    EventsProcessor<Order.State, Postgres.EventId, 'eventPayload, SqlIoError, 'eventHandlerIoError>
