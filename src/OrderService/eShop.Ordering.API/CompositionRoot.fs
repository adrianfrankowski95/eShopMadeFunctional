﻿[<RequireQualifiedAccess>]
module eShop.Ordering.API.CompositionRoot

open System
open System.Data
open System.Data.Common
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open RabbitMQ.Client
open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.API.PortsAdapters
open eShop.Ordering.Adapters
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Workflows
open eShop.Postgres
open eShop.RabbitMQ
open FsToolkit.ErrorHandling
open eShop.Prelude

// Note: This object is required since we can't just pass type GetService<'t> = unit -> 't
// and get different generic type each time the function is invoked.
// See: https://stackoverflow.com/questions/42598677/what-is-the-best-way-to-pass-generic-function-that-resolves-to-multiple-types
// Possible workaround: https://github.com/eiriktsarpalis/TypeShape/tree/main
type private Services =
    abstract Get<'t> : unit -> 't

module private Services =
    let fromSp (sp: IServiceProvider) =
        { new Services with
            member this.Get<'t>() = sp.GetService<'t>() }

    let fromCtx (ctx: HttpContext) =
        { new Services with
            member this.Get<'t>() = ctx.GetService<'t>() }


let private getDbConnection (services: Services) = services.Get<GetDbConnection>()

let private getStandaloneSqlSession (services: Services) =
    services |> getDbConnection |> SqlSession.Standalone

let getStandaloneSqlSessionFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> getStandaloneSqlSession


let inline private buildPersistEvents
    (services: Services)
    (persistEvents: DbTransaction -> PersistEvents<_, _, _, _>)
    : PersistEvents<_, _, _, _> =
    fun aggregateId events ->
        asyncResult {
            let connection = (services |> getDbConnection) ()

            do! connection.OpenAsync()

            let! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted).AsTask()

            return!
                persistEvents transaction aggregateId events
                |> AsyncResult.teeAsync (fun _ -> transaction.CommitAsync() |> Async.AwaitTask)
                |> AsyncResult.teeErrorAsync (fun _ -> transaction.RollbackAsync() |> Async.AwaitTask)
                |> AsyncResult.teeAnyAsync (connection.CloseAsync >> Async.AwaitTask)
        }


type OrderAggregateEventDispatcher = RabbitMQ.OrderAggregateEventDispatcher<EventId>

let private buildOrderAggregateEventDispatcher (services: Services) : string * OrderAggregateEventDispatcher =
    let rabbitMQConnection = services.Get<IConnection>()

    let jsonOptions = services.Get<JsonSerializerOptions>()

    (nameof RabbitMQ.OrderAggregateEventDispatcher),
    RabbitMQ.OrderAggregateEventDispatcher.create jsonOptions rabbitMQConnection

type OrderAggregateEventsProcessor =
    EventsProcessor<OrderAggregate.State, EventId, OrderAggregate.Event, SqlIoError, RabbitMQIoError>

let private buildOrderAggregateEventsProcessor services : OrderAggregateEventsProcessor =
    let sqlSession = services |> getStandaloneSqlSession
    let logger = services.Get<ILogger<OrderAggregateEventsProcessor>>()
    let eventsProcessorPort = services.Get<ISqlOrderAggregateEventsProcessorPort>()

    let persistEvents =
        eventsProcessorPort.PersistOrderEvents |> buildPersistEvents services

    let readEvents = sqlSession |> eventsProcessorPort.ReadUnprocessedOrderEvents

    let persistHandlers =
        sqlSession |> eventsProcessorPort.PersistSuccessfulEventHandlers

    let markEventsProcessed = sqlSession |> eventsProcessorPort.MarkEventAsProcessed

    let eventDispatcherName, eventDispatcher =
        services |> buildOrderAggregateEventDispatcher

    let logError (error: EventsProcessor.EventsProcessorError<'state, _, _, SqlIoError, _>) =
        match error with
        | EventsProcessor.ReadingUnprocessedEventsFailed eventLogIoError ->
            logger.LogError("Reading unprocessed events failed: {Error}", eventLogIoError)

        | EventsProcessor.PersistingSuccessfulEventHandlersFailed(AggregateId aggregateId,
                                                                  eventId,
                                                                  successfulHandlers,
                                                                  eventLogIoError) ->
            logger.LogError(
                "Persisting successful event handlers ({SuccessfulEventHandlers}) for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) failed: {Error}",
                (successfulHandlers |> String.concat ", "),
                eventId |> EventId.value,
                Aggregate.typeName<'state>,
                aggregateId,
                eventLogIoError
            )

        | EventsProcessor.MarkingEventAsProcessedFailed(eventId, eventLogIoError) ->
            logger.LogError(
                "Marking Event {EventId} as processed failed: {Error}",
                eventId |> EventId.value,
                eventLogIoError
            )

        | EventsProcessor.EventHandlerFailed(attempt,
                                             AggregateId aggregateId,
                                             eventId,
                                             event,
                                             handlerName,
                                             eventHandlingIoError) ->
            logger.LogError(
                "Handler {HandlerName} for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) failed after #{Attempt} attempt. Event data: {Event}, error: {Error}",
                handlerName,
                eventId |> EventId.value,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                event,
                eventHandlingIoError
            )

        | EventsProcessor.MaxEventProcessingRetriesReached(attempt,
                                                           AggregateId aggregateId,
                                                           eventId,
                                                           event,
                                                           handlerNames) ->
            logger.LogCritical(
                "Handlers {HandlerNames} for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) reached maximum attempts ({attempt}) and won't be retried. Event data: {Event}",
                (handlerNames |> String.concat ", "),
                eventId |> EventId.value,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                event
            )

    EventsProcessor.init
    |> EventsProcessor.registerHandler eventDispatcherName eventDispatcher
    |> EventsProcessor.withErrorHandler logError
    |> EventsProcessor.build persistEvents readEvents persistHandlers markEventsProcessed

let buildOrderAggregateEventsProcessorFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildOrderAggregateEventsProcessor


let private buildTransactionalWorkflowExecutor (services: Services) =
    services
    |> getDbConnection
    |> TransactionalWorkflowExecutor.init
    |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
    |> TransactionalWorkflowExecutor.execute

let private buildOrderWorkflowExecutor (services: Services) workflowToExecute =
    let orderPort = services.Get<ISqlOrderAggregateManagementPort>()

    let getUtcNow = services.Get<GetUtcNow>()

    let eventsProcessor = services.Get<OrderAggregateEventsProcessor>()

    fun dbTransaction ->
        let readOrder =
            dbTransaction |> SqlSession.Sustained |> orderPort.ReadOrderAggregate

        let persistOrder = dbTransaction |> orderPort.PersistOrderAggregate

        workflowToExecute
        |> WorkflowExecutor.execute getUtcNow readOrder persistOrder eventsProcessor.Process
    |> buildTransactionalWorkflowExecutor services


type EventDrivenOrderWorkflowError =
    | WorkflowExecutorError of WorkflowExecutorError<OrderAggregate.InvalidStateError, SqlIoError>
    | InvalidEventData of string

type EventDrivenOrderWorkflow =
    EventHandler<OrderAggregate.State, EventId, IntegrationEvent.Consumed, EventDrivenOrderWorkflowError>

let private buildEventDrivenOrderWorkflow services : string * EventDrivenOrderWorkflow =
    (nameof EventDrivenOrderWorkflow),
    fun orderAggregateId eventId integrationEvent ->
        let inline toWorkflowError x =
            x |> AsyncResult.mapError WorkflowExecutorError

        let inline executeWorkflow command workflow =
            buildOrderWorkflowExecutor services workflow orderAggregateId command

        match integrationEvent.Data with
        | IntegrationEvent.Consumed.GracePeriodConfirmed _ ->
            AwaitOrderStockItemsValidationWorkflow.build
            |> executeWorkflow ()
            |> toWorkflowError

        | IntegrationEvent.Consumed.OrderStockConfirmed _ ->
            ConfirmOrderItemsStockWorkflow.build |> executeWorkflow () |> toWorkflowError

        | IntegrationEvent.Consumed.OrderStockRejected orderStockRejected ->
            asyncResult {
                let! command =
                    orderStockRejected.OrderStockItems
                    |> List.filter (_.HasStock >> not)
                    |> List.map (_.ProductId >> ProductId.ofInt)
                    |> NonEmptyList.ofList
                    |> Result.map (fun items ->
                        ({ RejectedOrderItems = items }: OrderAggregate.Command.SetStockRejectedOrderStatus))
                    |> Result.setError (
                        $"Invalid event %s{(eventId |> EventId.value).ToString()} of type %s{nameof IntegrationEvent.Consumed.OrderStockRejected}: no rejected stock found"
                        |> InvalidEventData
                    )

                return!
                    RejectOrderItemsStockWorkflow.build
                    |> executeWorkflow command
                    |> toWorkflowError
            }

        | IntegrationEvent.Consumed.OrderPaymentFailed _ ->
            CancelOrderWorkflow.build |> executeWorkflow () |> toWorkflowError

        | IntegrationEvent.Consumed.OrderPaymentSucceeded _ ->
            PayOrderWorkflow.build |> executeWorkflow () |> toWorkflowError

type OrderIntegrationEventsProcessor =
    EventsProcessor<OrderAggregate.State, EventId, IntegrationEvent.Consumed, SqlIoError, EventDrivenOrderWorkflowError>

let private buildOrderIntegrationEventsProcessor services : OrderIntegrationEventsProcessor =
    let sqlSession = services |> getStandaloneSqlSession
    let config = services.Get<IOptions<Configuration.RabbitMQOptions>>()
    let logger = services.Get<ILogger<OrderIntegrationEventsProcessor>>()
    let eventsProcessorPort = services.Get<ISqlOrderIntegrationEventsProcessorPort>()

    let persistEvents =
        eventsProcessorPort.PersistOrderEvents |> buildPersistEvents services

    let readEvents = sqlSession |> eventsProcessorPort.ReadUnprocessedOrderEvents

    let persistHandlers =
        sqlSession |> eventsProcessorPort.PersistSuccessfulEventHandlers

    let markEventsProcessed = sqlSession |> eventsProcessorPort.MarkEventAsProcessed

    let retries =
        fun (attempt: int) -> TimeSpan.FromSeconds(Math.Pow(2, attempt))
        |> List.init config.Value.RetryCount

    let eventDrivenOrderWorkflowName, eventDrivenOrderWorkflow =
        services |> buildEventDrivenOrderWorkflow

    let logError (error: EventsProcessor.EventsProcessorError<'state, _, _, SqlIoError, _>) =
        match error with
        | EventsProcessor.ReadingUnprocessedEventsFailed eventLogIoError ->
            logger.LogError("Reading unprocessed events failed: {Error}", eventLogIoError)

        | EventsProcessor.PersistingSuccessfulEventHandlersFailed(AggregateId aggregateId,
                                                                  eventId,
                                                                  successfulHandlers,
                                                                  eventLogIoError) ->
            logger.LogError(
                "Persisting successful event handlers ({SuccessfulEventHandlers}) for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) failed: {Error}",
                (successfulHandlers |> String.concat ", "),
                eventId |> EventId.value,
                Aggregate.typeName<'state>,
                aggregateId,
                eventLogIoError
            )

        | EventsProcessor.MarkingEventAsProcessedFailed(eventId, eventLogIoError) ->
            logger.LogError("Marking Event {EventId} as processed failed: {Error}", eventId, eventLogIoError)

        | EventsProcessor.EventHandlerFailed(attempt,
                                             AggregateId aggregateId,
                                             eventId,
                                             event,
                                             handlerName,
                                             eventHandlingIoError) ->
            logger.LogError(
                "Handler {HandlerName} for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) failed after #{Attempt} attempt. Event data: {Event}, error: {Error}",
                handlerName,
                eventId |> EventId.value,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                event,
                eventHandlingIoError
            )

        | EventsProcessor.MaxEventProcessingRetriesReached(attempt,
                                                           AggregateId aggregateId,
                                                           eventId,
                                                           event,
                                                           handlerNames) ->
            logger.LogCritical(
                "Handlers {HandlerNames} for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) reached maximum attempts ({attempt}) and won't be retried. Event data: {Event}",
                (handlerNames |> String.concat ", "),
                eventId |> EventId.value,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                event
            )

    EventsProcessor.init
    |> EventsProcessor.withRetries retries
    |> EventsProcessor.registerHandler eventDrivenOrderWorkflowName eventDrivenOrderWorkflow
    |> EventsProcessor.withErrorHandler logError
    |> EventsProcessor.build persistEvents readEvents persistHandlers markEventsProcessed

let buildOrderIntegrationEventsProcessorFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildOrderIntegrationEventsProcessor


// let buildStartOrderWorkflow (services: Services) =
//     services |> buildOrderWorkflowExecutor StartOrderWorkflow
