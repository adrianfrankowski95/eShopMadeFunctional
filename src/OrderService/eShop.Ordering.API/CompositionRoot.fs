[<RequireQualifiedAccess>]
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
open eShop.Ordering.Adapters.Http
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Workflows
open eShop.Postgres
open eShop.RabbitMQ
open FsToolkit.ErrorHandling
open eShop.Prelude
open eShop.Prelude.Operators

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
    (persistEvents: DbTransaction -> PersistEvents<_, _, _>)
    : PersistEvents<_, _, _> =
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

type OrderAggregateEventDispatcher = EventHandler<OrderAggregate.State, OrderAggregate.Event, RabbitMQIoError>

let private buildOrderAggregateEventDispatcher (services: Services) : OrderAggregateEventDispatcher =
    let rabbitMQConnection = services.Get<IConnection>()

    let jsonOptions = services.Get<JsonSerializerOptions>()

    fun orderAggregateId orderAggregateEvent ->
        orderAggregateEvent
        |> Event.mapPayload (IntegrationEvent.Published.ofAggregateEvent orderAggregateId)
        |> RabbitMQ.OrderIntegrationEventDispatcher.create jsonOptions rabbitMQConnection

type OrderAggregateEventsProcessor =
    EventsProcessor<OrderAggregate.State, OrderAggregate.Event, SqlIoError, RabbitMQIoError>

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

    let errorLogger (error: EventsProcessor.EventsProcessorError<'state, _, SqlIoError, _>) =
        match error with
        | EventsProcessor.ReadingUnprocessedEventsFailed eventLogIoError ->
            logger.LogError("Reading unprocessed events failed: {Error}", eventLogIoError)

        | EventsProcessor.PersistingSuccessfulEventHandlersFailed(AggregateId aggregateId,
                                                                  event,
                                                                  successfulHandlers,
                                                                  eventLogIoError) ->
            logger.LogError(
                "Persisting successful event handlers ({SuccessfulEventHandlers}) for Event {Event} (Aggregate {AggregateType} - {AggregateId}) failed: {Error}",
                (successfulHandlers |> String.concat ", "),
                event,
                Aggregate.typeName<'state>,
                aggregateId,
                eventLogIoError
            )

        | EventsProcessor.MarkingEventAsProcessedFailed(event, eventLogIoError) ->
            logger.LogError("Marking Event {Event} as processed failed: {Error}", event, eventLogIoError)

        | EventsProcessor.EventHandlerFailed(attempt, AggregateId aggregateId, event, handlerName, eventHandlingIoError) ->
            logger.LogError(
                "Handler {HandlerName} for Event {Event} (Aggregate {AggregateType} - {AggregateId}) failed after #{Attempt} attempt. Error: {Error}",
                handlerName,
                event,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                eventHandlingIoError
            )

        | EventsProcessor.MaxEventProcessingRetriesReached(attempt, AggregateId aggregateId, event, handlerNames) ->
            logger.LogCritical(
                "Handlers {HandlerNames} for Event {Event} (Aggregate {AggregateType} - {AggregateId}) reached maximum attempts ({attempt}) and won't be retried",
                (handlerNames |> String.concat ", "),
                event,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt
            )

    let eventDispatcher = services |> buildOrderAggregateEventDispatcher

    let eventLogger =
        fun (aggregateId: AggregateId<'state>) event ->
            logger.LogInformation(
                "An Event occurred for Aggregate {AggregateType} - {AggregateId}: {Event}",
                Aggregate.typeName<'state>,
                aggregateId |> AggregateId.value,
                event
            )
            |> AsyncResult.ok

    EventsProcessor.init
    |> EventsProcessor.registerHandler (nameof RabbitMQ.OrderIntegrationEventDispatcher) eventDispatcher
    |> EventsProcessor.registerHandler "EventLogger" eventLogger
    |> EventsProcessor.withErrorHandler errorLogger
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

    let generateEventId = services.Get<GenerateId<eventId>>()

    let eventsProcessor = services.Get<OrderAggregateEventsProcessor>()

    fun dbTransaction ->
        let readOrder =
            dbTransaction |> SqlSession.Sustained |> orderPort.ReadOrderAggregate

        let persistOrder = dbTransaction |> orderPort.PersistOrderAggregate

        workflowToExecute
        |> WorkflowExecutor.execute getUtcNow generateEventId readOrder persistOrder eventsProcessor.Process
    |> buildTransactionalWorkflowExecutor services


type EventDrivenOrderWorkflowDispatcherError =
    | WorkflowExecutorError of WorkflowExecutorError<OrderAggregate.InvalidStateError, SqlIoError>
    | InvalidEventData of string

type EventDrivenOrderWorkflowDispatcher =
    EventHandler<OrderAggregate.State, IntegrationEvent.Consumed, EventDrivenOrderWorkflowDispatcherError>

let private buildEventDrivenOrderWorkflow services : EventDrivenOrderWorkflowDispatcher =
    fun orderAggregateId integrationEvent ->
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
                        $"Invalid event %s{integrationEvent.Id |> EventId.toString} of type %s{nameof IntegrationEvent.Consumed.OrderStockRejected}: no rejected stock found"
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
    EventsProcessor<OrderAggregate.State, IntegrationEvent.Consumed, SqlIoError, EventDrivenOrderWorkflowDispatcherError>

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

    let errorLogger (error: EventsProcessor.EventsProcessorError<'state, _, SqlIoError, _>) =
        match error with
        | EventsProcessor.ReadingUnprocessedEventsFailed eventLogIoError ->
            logger.LogError("Reading unprocessed events failed: {Error}", eventLogIoError)

        | EventsProcessor.PersistingSuccessfulEventHandlersFailed(AggregateId aggregateId,
                                                                  event,
                                                                  successfulHandlers,
                                                                  eventLogIoError) ->
            logger.LogError(
                "Persisting successful event handlers ({SuccessfulEventHandlers}) for Event {Event} (Aggregate {AggregateType} - {AggregateId}) failed: {Error}",
                (successfulHandlers |> String.concat ", "),
                event,
                Aggregate.typeName<'state>,
                aggregateId,
                eventLogIoError
            )

        | EventsProcessor.MarkingEventAsProcessedFailed(event, eventLogIoError) ->
            logger.LogError("Marking Event {Event} as processed failed: {Error}", event, eventLogIoError)

        | EventsProcessor.EventHandlerFailed(attempt, AggregateId aggregateId, event, handlerName, eventHandlingIoError) ->
            logger.LogError(
                "Handler {HandlerName} for Event {Event} (Aggregate {AggregateType} - {AggregateId}) failed after #{Attempt} attempt. Error: {Error}",
                handlerName,
                event,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                eventHandlingIoError
            )

        | EventsProcessor.MaxEventProcessingRetriesReached(attempt, AggregateId aggregateId, event, handlerNames) ->
            logger.LogCritical(
                "Handlers {HandlerNames} for Event {Event} (Aggregate {AggregateType} - {AggregateId}) reached maximum attempts ({attempt}) and won't be retried",
                (handlerNames |> String.concat ", "),
                event,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt
            )

    let eventDrivenOrderWorkflow = services |> buildEventDrivenOrderWorkflow

    let eventLogger =
        fun (aggregateId: AggregateId<'state>) event ->
            logger.LogInformation(
                "An Event occurred for Aggregate {AggregateType} - {AggregateId}: {Event}",
                Aggregate.typeName<'state>,
                aggregateId |> AggregateId.value,
                event
            )
            |> AsyncResult.ok

    EventsProcessor.init
    |> EventsProcessor.withRetries retries
    |> EventsProcessor.registerHandler (nameof EventDrivenOrderWorkflowDispatcher) eventDrivenOrderWorkflow
    |> EventsProcessor.registerHandler "EventLogger" eventLogger
    |> EventsProcessor.withErrorHandler errorLogger
    |> EventsProcessor.build persistEvents readEvents persistHandlers markEventsProcessed

let buildOrderIntegrationEventsProcessorFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildOrderIntegrationEventsProcessor

type OrderWorkflowIoError = Either<SqlIoError, HttpIoError>

let private buildStartOrderWorkflow (services: Services) =
    let sqlPaymentPort = services.Get<ISqlPaymentManagementPort>()
    let sqlOrderPort = services.Get<ISqlOrderAggregateManagementPort>()
    let paymentPort = services.Get<IPaymentManagementPort<HttpIoError>>()

    let getUtcNow = services.Get<GetUtcNow>()

    let generateEventId = services.Get<GenerateId<eventId>>()

    let processEvents =
        services.Get<OrderAggregateEventsProcessor>().Process
        >>> AsyncResult.mapError Left

    let verifyPaymentMethod =
        paymentPort.VerifyPaymentMethod >> AsyncResult.mapError (Either.mapRight Right)

    fun dbTransaction ->
        let readOrder =
            dbTransaction
            |> SqlSession.Sustained
            |> sqlOrderPort.ReadOrderAggregate
            >> AsyncResult.mapError Left

        let persistOrder =
            dbTransaction |> sqlOrderPort.PersistOrderAggregate
            >>> AsyncResult.mapError Left

        let getSupportedCardTypes =
            dbTransaction
            |> SqlSession.Sustained
            |> sqlPaymentPort.GetSupportedCardTypes
            >> AsyncResult.mapError Left

        StartOrderWorkflow.build getSupportedCardTypes verifyPaymentMethod
        |> WorkflowExecutor.execute getUtcNow generateEventId readOrder persistOrder processEvents
    |> buildTransactionalWorkflowExecutor services

let buildStartOrderWorkflowFromCtx
    (ctx: HttpContext)
    : Workflow<OrderAggregate.State, StartOrderWorkflow.Command, _, _> =
    ctx |> Services.fromCtx |> buildStartOrderWorkflow
