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

type ISqlOrderIntegrationEventsPort<'ioError> =
    abstract member PersistOrderIntegrationEvents:
        DbTransaction -> PersistEvents<Order.State, IntegrationEvent.Consumed, 'ioError>

    abstract member ReadUnprocessedOrderIntegrationEvents:
        SqlSession -> ReadUnprocessedEvents<Order.State, IntegrationEvent.Consumed, 'ioError>

    abstract member PersistSuccessfulOrderIntegrationEventHandlers:
        SqlSession -> PersistSuccessfulEventHandlers<'ioError>

    abstract member MarkOrderIntegrationEventAsProcessed: SqlSession -> MarkEventAsProcessed<'ioError>

type ISqlOrderIntegrationEventsAdapter = ISqlOrderIntegrationEventsPort<SqlIoError>

type PostgresOrderIntegrationEventsAdapter(jsonOptions: JsonSerializerOptions, dbSchema: DbSchema, getNow: GetUtcNow) =
    interface ISqlOrderIntegrationEventsAdapter with
        member this.PersistOrderIntegrationEvents(dbTransaction) =
            OrderIntegrationEventsAdapter.persistOrderIntegrationEvents jsonOptions dbSchema dbTransaction

        member this.PersistSuccessfulOrderIntegrationEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkOrderIntegrationEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow

        member this.ReadUnprocessedOrderIntegrationEvents(sqlSession) =
            OrderIntegrationEventsAdapter.readUnprocessedOrderIntegrationEvents jsonOptions dbSchema sqlSession


type ISqlOrderAggregateManagementPort<'ioError> =
    abstract member ReadOrderAggregate: SqlSession -> OrderAggregateManagementPort.ReadOrderAggregate<'ioError>

    abstract member PersistOrderAggregate: DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregate<'ioError>

    abstract member PersistOrderAggregateEvents:
        DbTransaction -> OrderAggregateManagementPort.PersistOrderAggregateEvents<'ioError>

    abstract member ReadUnprocessedOrderAggregateEvents:
        SqlSession -> ReadUnprocessedEvents<Order.State, Order.Event, 'ioError>

    abstract member PersistSuccessfulOrderAggregateEventHandlers: SqlSession -> PersistSuccessfulEventHandlers<'ioError>

    abstract member MarkOrderAggregateEventAsProcessed: SqlSession -> MarkEventAsProcessed<'ioError>

type ISqlOrderAggregateManagementAdapter = ISqlOrderAggregateManagementPort<SqlIoError>

type PostgresOrderAggregateManagementAdapter(jsonOptions: JsonSerializerOptions, dbSchema: DbSchema, getNow: GetUtcNow)
    =
    interface ISqlOrderAggregateManagementAdapter with
        member this.ReadOrderAggregate(sqlSession) =
            OrderAggregateManagementAdapter.readOrderAggregate dbSchema sqlSession

        member this.PersistOrderAggregate(dbTransaction) =
            OrderAggregateManagementAdapter.persistOrderAggregate dbSchema dbTransaction

        member this.PersistOrderAggregateEvents(dbTransaction) =
            OrderAggregateManagementAdapter.persistOrderAggregateEvents jsonOptions dbSchema dbTransaction

        member this.ReadUnprocessedOrderAggregateEvents(sqlSession) =
            OrderAggregateManagementAdapter.readUnprocessedOrderAggregateEvents jsonOptions dbSchema sqlSession

        member this.PersistSuccessfulOrderAggregateEventHandlers(sqlSession) =
            Postgres.persistSuccessfulEventHandlers dbSchema sqlSession

        member this.MarkOrderAggregateEventAsProcessed(sqlSession) =
            Postgres.markEventAsProcessed dbSchema sqlSession getNow


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
