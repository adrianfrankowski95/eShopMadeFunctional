module eShop.Ordering.API.PortsAdapters

open System.Data.Common
open eShop.Ordering.Domain.Ports
open eShop.Postgres
open eShop.DomainDrivenDesign.Postgres

type ISqlOrderManagementPort<'eventId when 'eventId: comparison> =
    abstract member ReadOrderAggregate: SqlSession -> OrderManagementPort.PersistOrderAggregate<SqlIoError>
    abstract member PersistOrderAggregate: DbTransaction -> OrderManagementPort.PersistOrderAggregate<SqlIoError>
    abstract member PersistOrderEvents: DbTransaction -> OrderManagementPort.PersistOrderEvents<'eventId, SqlIoError>
    abstract member PublishOrderEvents: OrderManagementPort.PublishOrderEvents<'eventId, SqlIoError>

    abstract member ReadUnprocessedOrderEvents:
        SqlSession -> OrderManagementPort.ReadUnprocessedOrderEvents<'eventId, SqlIoError>

    abstract member GetSupportedCardTypes: SqlSession -> OrderManagementPort.GetSupportedCardTypes<SqlIoError>

type PostgresOrderManagementAdapter = ISqlOrderManagementPort<Postgres.EventId>
