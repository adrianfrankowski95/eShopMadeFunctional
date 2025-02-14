[<RequireQualifiedAccess>]
module Ordering.Domain.Ports.OrderManagementPort

open eShop.DomainDrivenDesign
open Ordering.Domain.Model

type ReadOrderAggregate<'ioError> = ReadAggregate<Order, 'ioError>

type PersistOrderAggregate<'ioError> = PersistAggregate<Order, 'ioError>

type PersistOrderEvents<'eventId, 'ioError> = PersistEvents<Order, 'eventId, DomainEvent, 'ioError>

type PublishOrderEvents<'eventId, 'ioError> = PublishEvents<'eventId, DomainEvent, 'ioError>

