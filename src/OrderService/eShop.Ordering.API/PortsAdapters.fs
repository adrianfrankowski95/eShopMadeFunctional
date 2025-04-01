module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Adapters.Postgres

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
            OrderAdapter.readUnprocessedOrderAggregateEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

type IPostgresOrderIntegrationEventsProcessorAdapter =
    inherit ISqlOrderEventsProcessorPort<Postgres.EventId, IntegrationEvent.Consumed>

    abstract member PersistOrderIntegrationEvent: SqlSession -> OrderAdapter.PersistOrderIntegrationEvent

type PostgresOrderIntegrationEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface IPostgresOrderIntegrationEventsProcessorAdapter with
        member this.ReadUnprocessedOrderEvents(sqlSession) =
            OrderAdapter.readUnprocessedOrderIntegrationEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

        member this.PersistOrderIntegrationEvent(sqlSession) =
            OrderAdapter.persistOrderIntegrationEvent dbSchema sqlSession

type ISqlOrderAggregateManagementPort<'eventId> =
    abstract member ReadOrderAggregate: SqlSession -> OrderAggregateManagementPort.ReadOrderAggregate<SqlIoError>

    abstract member PersistOrderAggregate:
        DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregate<SqlIoError>

    abstract member PersistOrderAggregateEvents:
        DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregateEvents<'eventId, SqlIoError>

    abstract member GetSupportedCardTypes: SqlSession -> OrderAggregateManagementPort.GetSupportedCardTypes<SqlIoError>

type IPostgresOrderAggregateManagementAdapter = ISqlOrderAggregateManagementPort<Postgres.EventId>

type PostgresOrderAggregateManagementAdapter(dbSchema: DbSchema) =
    interface IPostgresOrderAggregateManagementAdapter with
        member this.ReadOrderAggregate(sqlSession) =
            OrderAdapter.readOrderAggregate dbSchema sqlSession

        member this.PersistOrderAggregate(dbTransaction) =
            OrderAdapter.persistOrderAggregate dbSchema dbTransaction

        member this.PersistOrderAggregateEvents(dbTransaction) =
            OrderAdapter.persistOrderAggregateEvents dbSchema dbTransaction

        member this.GetSupportedCardTypes(sqlSession) =
            OrderAdapter.getSupportedCardTypes dbSchema sqlSession
