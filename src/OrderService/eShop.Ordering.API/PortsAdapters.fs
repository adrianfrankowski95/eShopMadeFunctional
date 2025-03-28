module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open FsToolkit.ErrorHandling
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Adapters.Postgres
open eShop.Prelude.Operators
open eShop.RabbitMQ

type ISqlOrderEventsProcessorPort<'eventId, 'eventPayload> =
    abstract member ReadUnprocessedOrderEvents:
        SqlSession -> ReadUnprocessedEvents<OrderAggregate.State, 'eventId, 'eventPayload, SqlIoError>

    abstract member PersistSuccessfulEventHandlers: SqlSession -> PersistSuccessfulEventHandlers<'eventId, SqlIoError>

    abstract member MarkEventAsProcessed: SqlSession -> MarkEventAsProcessed<'eventId, SqlIoError>

type IPostgresOrderAggregateEventsProcessorAdapter =
    ISqlOrderEventsProcessorPort<Postgres.EventId, OrderAggregate.Event>

type PostgresOrderAggregateEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface IPostgresOrderAggregateEventsProcessorAdapter with
        member this.ReadUnprocessedOrderEvents(sqlSession) =
            PostgresOrderAggregateManagementAdapter.readUnprocessedOrderAggregateEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

type IPostgresOrderIntegrationEventsProcessorAdapter =
    inherit ISqlOrderEventsProcessorPort<Postgres.EventId, IntegrationEvent.Consumed>

    abstract member PersistOrderIntegrationEvents:
        DbTransaction -> PostgresOrderIntegrationEventManagementAdapter.PersistOrderIntegrationEvents

type PostgresOrderIntegrationEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface IPostgresOrderIntegrationEventsProcessorAdapter with
        member this.ReadUnprocessedOrderEvents(sqlSession) =
            PostgresOrderIntegrationEventManagementAdapter.readUnprocessedOrderIntegrationEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

        member this.PersistOrderIntegrationEvents(dbTransaction) =
            PostgresOrderIntegrationEventManagementAdapter.persistOrderIntegrationEvents dbSchema dbTransaction

type ISqlOrderAggregateManagementPort<'eventId> =
    abstract member ReadOrderAggregate: SqlSession -> OrderAggregateManagementPort.ReadOrderAggregate<SqlIoError>

    abstract member PersistOrderAggregate:
        DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregate<SqlIoError>

    abstract member PersistOrderAggregateEvents:
        DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregateEvents<'eventId, SqlIoError>

    abstract member PublishOrderAggregateEvents:
        OrderAggregateManagementPort.PublishOrderAggregateEvents<'eventId, SqlIoError>

    abstract member GetSupportedCardTypes: SqlSession -> OrderAggregateManagementPort.GetSupportedCardTypes<SqlIoError>

type IPostgresOrderAggregateManagementAdapter = ISqlOrderAggregateManagementPort<Postgres.EventId>

type PostgresOrderAggregateManagementAdapter
    (
        dbSchema: DbSchema,
        eventsProcessor: PostgresOrderAggregateManagementAdapter.OrderAggregateEventsProcessor<RabbitMQIoError>
    ) =
    interface IPostgresOrderAggregateManagementAdapter with
        member this.ReadOrderAggregate(sqlSession) =
            PostgresOrderAggregateManagementAdapter.readOrderAggregate dbSchema sqlSession

        member this.PersistOrderAggregate(dbTransaction) =
            PostgresOrderAggregateManagementAdapter.persistOrderAggregate dbSchema dbTransaction

        member this.PersistOrderAggregateEvents(dbTransaction) =
            PostgresOrderAggregateManagementAdapter.persistOrderAggregateEvents dbSchema dbTransaction

        member this.GetSupportedCardTypes(sqlSession) =
            PostgresOrderAggregateManagementAdapter.getSupportedCardTypes dbSchema sqlSession

        member this.PublishOrderAggregateEvents = eventsProcessor.Process >>> AsyncResult.ok
