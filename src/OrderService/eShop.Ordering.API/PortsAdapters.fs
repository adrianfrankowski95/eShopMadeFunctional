﻿module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open FsToolkit.ErrorHandling
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.Adapters.Postgres

type ISqlOrderManagementPort<'eventId when 'eventId: comparison> =
    abstract member ReadOrderAggregate: SqlSession -> OrderManagementPort.ReadOrderAggregate<SqlIoError>
    abstract member PersistOrderAggregate: DbTransaction -> OrderManagementPort.PersistOrderAggregate<SqlIoError>
    abstract member PersistOrderEvents: DbTransaction -> OrderManagementPort.PersistOrderEvents<'eventId, SqlIoError>
    abstract member PublishOrderEvents: OrderManagementPort.PublishOrderEvents<'eventId, SqlIoError>

    abstract member ReadUnprocessedOrderEvents:
        SqlSession -> OrderManagementPort.ReadUnprocessedOrderEvents<'eventId, SqlIoError>

    abstract member GetSupportedCardTypes: SqlSession -> OrderManagementPort.GetSupportedCardTypes<SqlIoError>

type IPostgresOrderManagementAdapter = ISqlOrderManagementPort<Postgres.EventId>

type PostgresOrderManagementAdapter<'eventHandlerIoError>
    (dbSchema: DbSchema, eventsProcessor: PostgresOrderManagementAdapter.OrderEventsProcessor<'eventHandlerIoError>) =
    interface IPostgresOrderManagementAdapter with
        member this.ReadOrderAggregate(sqlSession) =
            PostgresOrderManagementAdapter.readOrderAggregate dbSchema sqlSession

        member this.PersistOrderAggregate(dbTransaction) =
            PostgresOrderManagementAdapter.persistOrderAggregate dbSchema dbTransaction

        member this.PersistOrderEvents(dbTransaction) =
            PostgresOrderManagementAdapter.persistOrderEvents dbSchema dbTransaction

        member this.PublishOrderEvents = eventsProcessor.Publish >> AsyncResult.ok

        member this.ReadUnprocessedOrderEvents(sqlSession) =
            PostgresOrderManagementAdapter.readUnprocessedOrderEvents dbSchema sqlSession

        member this.GetSupportedCardTypes(sqlSession) =
            PostgresOrderManagementAdapter.getSupportedCardTypes dbSchema sqlSession
