module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Adapters.Postgres

type ISqlOrderEventsProcessorPort<'eventPayload> =
    abstract member PersistOrderEvents:
        DbTransaction -> PersistEvents<OrderAggregate.State, EventId, 'eventPayload, SqlIoError>

    abstract member ReadUnprocessedOrderEvents:
        SqlSession -> ReadUnprocessedEvents<OrderAggregate.State, EventId, 'eventPayload, SqlIoError>

    abstract member PersistSuccessfulEventHandlers: SqlSession -> PersistSuccessfulEventHandlers<EventId, SqlIoError>

    abstract member MarkEventAsProcessed: SqlSession -> MarkEventAsProcessed<EventId, SqlIoError>

type ISqlOrderAggregateEventsProcessorPort = ISqlOrderEventsProcessorPort<OrderAggregate.Event>

type PostgresOrderAggregateEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface ISqlOrderAggregateEventsProcessorPort with
        member this.PersistOrderEvents(dbTransaction) =
            OrderAggregateEventsProcessorAdapter.persistOrderAggregateEvents dbSchema dbTransaction

        member this.ReadUnprocessedOrderEvents(sqlSession) =
            OrderAggregateEventsProcessorAdapter.readUnprocessedOrderAggregateEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

type ISqlOrderIntegrationEventsProcessorPort = ISqlOrderEventsProcessorPort<IntegrationEvent.Consumed>

type PostgresOrderIntegrationEventsProcessorAdapter(dbSchema: DbSchema, getNow: GetUtcNow) =
    interface ISqlOrderIntegrationEventsProcessorPort with
        member this.PersistOrderEvents(dbTransaction) =
            OrderIntegrationEventsProcessorAdapter.persistOrderIntegrationEvents dbSchema dbTransaction

        member this.ReadUnprocessedOrderEvents(sqlSession) =
            OrderIntegrationEventsProcessorAdapter.readUnprocessedOrderIntegrationEvents dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow


type ISqlOrderAggregateManagementPort =
    abstract member ReadOrderAggregate: SqlSession -> OrderAggregateManagementPort.ReadOrderAggregate<SqlIoError>

    abstract member PersistOrderAggregate:
        DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregate<SqlIoError>

type PostgresOrderAggregateManagementAdapter(dbSchema: DbSchema) =
    interface ISqlOrderAggregateManagementPort with
        member this.ReadOrderAggregate(sqlSession) =
            OrderAggregateManagementAdapter.readOrderAggregate dbSchema sqlSession

        member this.PersistOrderAggregate(dbTransaction) =
            OrderAggregateManagementAdapter.persistOrderAggregate dbSchema dbTransaction


type ISqlPaymentManagementPort =
    abstract member GetSupportedCardTypes: SqlSession -> PaymentManagementPort.GetSupportedCardTypes<SqlIoError>

type PostgresPaymentManagementAdapter(dbSchema: DbSchema) =
    interface ISqlPaymentManagementPort with
        member this.GetSupportedCardTypes(sqlSession) =
            PaymentManagementAdapter.getSupportedCardTypes dbSchema sqlSession
