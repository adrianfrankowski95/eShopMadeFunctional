module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open FsToolkit.ErrorHandling
open eShop.DomainDrivenDesign
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Adapters.Postgres
open eShop.Prelude.Operators
open eShop.RabbitMQ
open eShop.Ordering.Adapters.RabbitMQ

type ISqlOrderEventsProcessorPort<'eventId, 'eventPayload> =
    abstract member ReadUnprocessedOrderEvents:
        SqlSession -> ReadUnprocessedEvents<Order.State, 'eventId, 'eventPayload, SqlIoError>

    abstract member PersistSuccessfulEventHandlers: SqlSession -> PersistSuccessfulEventHandlers<'eventId, SqlIoError>

    abstract member MarkEventAsProcessed: SqlSession -> MarkEventAsProcessed<'eventId, SqlIoError>

type IPostgresOrderAggregateEventsProcessorPort = ISqlOrderEventsProcessorPort<Postgres.EventId, Order.Event>

type PostgresOrderAggregateEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface IPostgresOrderAggregateEventsProcessorPort with
        member this.ReadUnprocessedOrderEvents(sqlSession) =
            PostgresOrderManagementAdapter.readUnprocessedOrderEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

type IPostgresOrderIntegrationEventsProcessorPort = ISqlOrderEventsProcessorPort<Postgres.EventId, IntegrationEvent.Consumed>

type PostgresOrderIntegrationEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface IPostgresOrderIntegrationEventsProcessorPort with
        member this.ReadUnprocessedOrderEvents(sqlSession) =
            PostgresOrderManagementAdapter.readUnprocessedOrderEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow
//
// type ISqlOrderManagementPort<'eventId> =
//     abstract member ReadOrderAggregate: SqlSession -> OrderManagementPort.ReadOrderAggregate<SqlIoError>
//     abstract member PersistOrderAggregate: DbTransaction -> OrderManagementPort.PersistOrderAggregate<SqlIoError>
//     abstract member PersistOrderEvents: DbTransaction -> OrderManagementPort.PersistOrderEvents<'eventId, SqlIoError>
//
//     abstract member PublishOrderEvents: OrderManagementPort.PublishOrderEvents<'eventId, SqlIoError>
//
//     abstract member ReadUnprocessedOrderEvents:
//         SqlSession -> OrderManagementPort.ReadUnprocessedOrderEvents<'eventId, SqlIoError>
//
//     abstract member GetSupportedCardTypes: SqlSession -> OrderManagementPort.GetSupportedCardTypes<SqlIoError>
//
// type IPostgresOrderManagementAdapter = ISqlOrderManagementPort<Postgres.EventId>
//
// type PostgresRabbitMQOrderManagementAdapter
//     (dbSchema: DbSchema, eventsProcessor: PostgresOrderManagementAdapter.OrderEventsProcessor<RabbitMQIoError>) =
//     interface IPostgresOrderManagementAdapter with
//         member this.ReadOrderAggregate(sqlSession) =
//             PostgresOrderManagementAdapter.readOrderAggregate dbSchema sqlSession
//
//         member this.PersistOrderAggregate(dbTransaction) =
//             PostgresOrderManagementAdapter.persistOrderAggregate dbSchema dbTransaction
//
//         member this.PersistOrderEvents(dbTransaction) =
//             PostgresOrderManagementAdapter.persistOrderEvents dbSchema dbTransaction
//
//         member this.ReadUnprocessedOrderEvents(sqlSession) =
//             PostgresOrderManagementAdapter.readUnprocessedOrderEvents dbSchema sqlSession
//
//         member this.GetSupportedCardTypes(sqlSession) =
//             PostgresOrderManagementAdapter.getSupportedCardTypes dbSchema sqlSession
//
//         member this.PublishOrderEvents = eventsProcessor.Process >>> AsyncResult.ok
//
//
// type ISqlOrderIntegrationEventManagementPort<'eventId> =
//     abstract member PersistOrderIntegrationEvents:
//         DbTransaction -> PersistEvents<Order.State, 'eventId, IntegrationEvent.Consumed, SqlIoError>
//
//     abstract member ProcessOrderIntegrationEvents:
//         PublishEvents<Order.State, 'eventId, IntegrationEvent.Consumed, SqlIoError>
//
//     abstract member ReadUnprocessedOrderIntegrationEvents:
//         SqlSession -> ReadUnprocessedEvents<Order.State, 'eventId, IntegrationEvent.Consumed, SqlIoError>
