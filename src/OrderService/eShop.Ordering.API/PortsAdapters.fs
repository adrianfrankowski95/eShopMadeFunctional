module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open System.Text.Json
open eShop.DomainDrivenDesign
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Adapters.Http
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Adapters.Postgres

type ISqlOrderEventsProcessorPort<'eventPayload, 'ioError> =
    abstract member PersistOrderEvents: DbTransaction -> PersistEvents<OrderAggregate.State, 'eventPayload, 'ioError>

    abstract member ReadUnprocessedOrderEvents:
        SqlSession -> ReadUnprocessedEvents<OrderAggregate.State, 'eventPayload, 'ioError>

    abstract member PersistSuccessfulEventHandlers: SqlSession -> PersistSuccessfulEventHandlers<'ioError>

    abstract member MarkEventAsProcessed: SqlSession -> MarkEventAsProcessed<'ioError>

type ISqlOrderAggregateEventsProcessorAdapter = ISqlOrderEventsProcessorPort<OrderAggregate.Event, SqlIoError>

type PostgresOrderAggregateEventsProcessorAdapter
    (jsonOptions: JsonSerializerOptions, dbSchema: DbSchema, getNow: GetUtcNow) =
    interface ISqlOrderAggregateEventsProcessorAdapter with
        member this.PersistOrderEvents(dbTransaction) =
            OrderAggregateEventsProcessorAdapter.persistOrderAggregateEvents jsonOptions dbSchema dbTransaction

        member this.ReadUnprocessedOrderEvents(sqlSession) =
            OrderAggregateEventsProcessorAdapter.readUnprocessedOrderAggregateEvents jsonOptions dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

type ISqlOrderIntegrationEventsProcessorAdapter = ISqlOrderEventsProcessorPort<IntegrationEvent.Consumed, SqlIoError>

type PostgresOrderIntegrationEventsProcessorAdapter
    (jsonOptions: JsonSerializerOptions, dbSchema: DbSchema, getNow: GetUtcNow) =
    interface ISqlOrderIntegrationEventsProcessorAdapter with
        member this.PersistOrderEvents(dbTransaction) =
            OrderIntegrationEventsProcessorAdapter.persistOrderIntegrationEvents jsonOptions dbSchema dbTransaction

        member this.ReadUnprocessedOrderEvents(sqlSession) =
            OrderIntegrationEventsProcessorAdapter.readUnprocessedOrderIntegrationEvents jsonOptions dbSchema sqlSession

        member this.PersistSuccessfulEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow


type ISqlOrderAggregateManagementPort<'ioError> =
    abstract member ReadOrderAggregate: SqlSession -> OrderAggregateManagementPort.ReadOrderAggregate<'ioError>

    abstract member PersistOrderAggregate: DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregate<'ioError>


type ISqlOrderAggregateManagementAdapter = ISqlOrderAggregateManagementPort<SqlIoError>

type PostgresOrderAggregateManagementAdapter(dbSchema: DbSchema) =
    interface ISqlOrderAggregateManagementAdapter with
        member this.ReadOrderAggregate(sqlSession) =
            OrderAggregateManagementAdapter.readOrderAggregate dbSchema sqlSession

        member this.PersistOrderAggregate(dbTransaction) =
            OrderAggregateManagementAdapter.persistOrderAggregate dbSchema dbTransaction


type ISqlPaymentManagementPort<'ioError> =
    abstract member GetSupportedCardTypes: SqlSession -> PaymentManagementPort.GetSupportedCardTypes<'ioError>

type ISqlPaymentManagementAdapter = ISqlPaymentManagementPort<SqlIoError>

type PostgresPaymentManagementAdapter(dbSchema: DbSchema) =
    interface ISqlPaymentManagementAdapter with
        member this.GetSupportedCardTypes(sqlSession) =
            PaymentManagementAdapter.getSupportedCardTypes dbSchema sqlSession


type IPaymentManagementPort<'ioError> =
    abstract member VerifyPaymentMethod: PaymentManagementPort.VerifyPaymentMethod<'ioError>

type IHttpPaymentManagementAdapter = IPaymentManagementPort<HttpIoError>

type HttpPaymentManagementAdapter(shouldAcceptPayment) =

    interface IHttpPaymentManagementAdapter with
        member this.VerifyPaymentMethod =
            PaymentManagementAdapter.verifyPaymentMethod shouldAcceptPayment
