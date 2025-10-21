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
        DbConnection -> PersistEvents<Order.State, IntegrationEvent.Consumed, 'ioError>

    abstract member ReadUnprocessedOrderIntegrationEvents:
        DbConnection -> ReadUnprocessedEvents<Order.State, IntegrationEvent.Consumed, 'ioError>

    abstract member PersistSuccessfulOrderIntegrationEventHandlers:
        DbConnection -> PersistSuccessfulEventHandlers<'ioError>

    abstract member MarkOrderIntegrationEventAsProcessed: DbConnection -> MarkEventAsProcessed<'ioError>

type ISqlOrderIntegrationEventsAdapter = ISqlOrderIntegrationEventsPort<SqlIoError>

type PostgresOrderIntegrationEventsAdapter(jsonOptions: JsonSerializerOptions, dbSchema: DbSchema, getNow: GetUtcNow) =
    interface ISqlOrderIntegrationEventsAdapter with
        member this.PersistOrderIntegrationEvents(dbConnection) =
            OrderIntegrationEventsAdapter.persistOrderIntegrationEvents jsonOptions dbSchema dbConnection

        member this.PersistSuccessfulOrderIntegrationEventHandlers(dbConnection) =
            Postgres.persistSuccessfulEventHandlers dbSchema dbConnection

        member this.MarkOrderIntegrationEventAsProcessed(dbConnection) =
            Postgres.markEventAsProcessed dbSchema dbConnection getNow

        member this.ReadUnprocessedOrderIntegrationEvents(dbConnection) =
            OrderIntegrationEventsAdapter.readUnprocessedOrderIntegrationEvents jsonOptions dbSchema dbConnection


type ISqlOrderAggregateManagementPort<'ioError> =
    abstract member ReadOrderAggregate: DbConnection -> OrderAggregateManagementPort.ReadOrderAggregate<'ioError>

    abstract member PersistOrderAggregate: DbConnection -> OrderAggregateManagementPort.PersistOrderAggregate<'ioError>

    abstract member PersistOrderAggregateEvents:
        DbConnection -> OrderAggregateManagementPort.PersistOrderAggregateEvents<'ioError>

    abstract member ReadUnprocessedOrderAggregateEvents:
        DbConnection -> ReadUnprocessedEvents<Order.State, Order.Event, 'ioError>

    abstract member PersistSuccessfulOrderAggregateEventHandlers:
        DbConnection -> PersistSuccessfulEventHandlers<'ioError>

    abstract member MarkOrderAggregateEventAsProcessed: DbConnection -> MarkEventAsProcessed<'ioError>

type ISqlOrderAggregateManagementAdapter = ISqlOrderAggregateManagementPort<SqlIoError>

type PostgresOrderAggregateManagementAdapter(jsonOptions: JsonSerializerOptions, dbSchema: DbSchema, getNow: GetUtcNow)
    =
    interface ISqlOrderAggregateManagementAdapter with
        member this.ReadOrderAggregate(dbConnection) =
            OrderAggregateManagementAdapter.readOrderAggregate dbSchema dbConnection

        member this.PersistOrderAggregate(dbTransaction) =
            OrderAggregateManagementAdapter.persistOrderAggregate dbSchema dbTransaction

        member this.PersistOrderAggregateEvents(dbTransaction) =
            OrderAggregateManagementAdapter.persistOrderAggregateEvents jsonOptions dbSchema dbTransaction

        member this.ReadUnprocessedOrderAggregateEvents(dbConnection) =
            OrderAggregateManagementAdapter.readUnprocessedOrderAggregateEvents jsonOptions dbSchema dbConnection

        member this.PersistSuccessfulOrderAggregateEventHandlers(dbConnection) =
            Postgres.persistSuccessfulEventHandlers dbSchema dbConnection

        member this.MarkOrderAggregateEventAsProcessed(dbConnection) =
            Postgres.markEventAsProcessed dbSchema dbConnection getNow


type ISqlPaymentManagementPort<'ioError> =
    abstract member GetSupportedCardTypes: DbConnection -> PaymentManagementPort.GetSupportedCardTypes<'ioError>

type ISqlPaymentManagementAdapter = ISqlPaymentManagementPort<SqlIoError>

type PostgresPaymentManagementAdapter(dbSchema: DbSchema) =
    interface ISqlPaymentManagementAdapter with
        member this.GetSupportedCardTypes(dbConnection) =
            PaymentManagementAdapter.getSupportedCardTypes dbSchema dbConnection


type IPaymentManagementPort<'ioError> =
    abstract member VerifyPaymentMethod: PaymentManagementPort.VerifyPaymentMethod<'ioError>

type IHttpPaymentManagementAdapter = IPaymentManagementPort<HttpIoError>

type HttpPaymentManagementAdapter(shouldAcceptPayment) =

    interface IHttpPaymentManagementAdapter with
        member this.VerifyPaymentMethod =
            PaymentManagementAdapter.verifyPaymentMethod shouldAcceptPayment
