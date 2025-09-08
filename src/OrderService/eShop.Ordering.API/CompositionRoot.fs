[<RequireQualifiedAccess>]
module eShop.Ordering.API.CompositionRoot

open System
open System.Data
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


let private buildPersistIntegrationEvents (services: Services) : PersistEvents<_, _, _> =
    fun aggregateId events ->
        asyncResult {
            let persistIntegrationEvents =
                services.Get<ISqlOrderIntegrationEventsAdapter>().PersistOrderIntegrationEvents

            let connection = (services |> getDbConnection) ()

            do! connection.OpenAsync()

            let! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted).AsTask()

            return!
                persistIntegrationEvents transaction aggregateId events
                |> AsyncResult.teeAsync (fun _ -> transaction.CommitAsync() |> Async.AwaitTask)
                |> AsyncResult.teeErrorAsync (fun _ -> transaction.RollbackAsync() |> Async.AwaitTask)
                |> AsyncResult.teeAnyAsync (connection.CloseAsync >> Async.AwaitTask)
        }

let buildPersistIntegrationEventsFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildPersistIntegrationEvents


type OrderAggregateEventDispatcher = EventHandler<Order.State, Order.Event, RabbitMQIoError>

let private buildOrderAggregateEventDispatcher (services: Services) : OrderAggregateEventDispatcher =
    let rabbitMQConnection = services.Get<IConnection>()

    let jsonOptions = services.Get<JsonSerializerOptions>()

    fun orderAggregateId orderAggregateEvent ->
        orderAggregateEvent
        |> Event.mapPayload (IntegrationEvent.Published.ofAggregateEvent orderAggregateId)
        |> RabbitMQ.OrderIntegrationEventDispatcher.create jsonOptions rabbitMQConnection

type OrderAggregateEventsProcessor = EventsProcessor<Order.State, Order.Event, SqlIoError, RabbitMQIoError>

let private buildOrderAggregateEventsProcessor services : OrderAggregateEventsProcessor =
    let sqlSession = services |> getStandaloneSqlSession
    let logger = services.Get<ILogger<OrderAggregateEventsProcessor>>()

    let orderAggregateAdapter = services.Get<ISqlOrderAggregateManagementAdapter>()

    let readEvents =
        sqlSession |> orderAggregateAdapter.ReadUnprocessedOrderAggregateEvents

    let persistHandlers =
        sqlSession |> orderAggregateAdapter.PersistSuccessfulOrderAggregateEventHandlers

    let markEventsProcessed =
        sqlSession |> orderAggregateAdapter.MarkOrderAggregateEventAsProcessed

    let logError = Logger.logEventsProcessorError logger

    let logEvent = Logger.logEvent logger

    let eventDispatcher = services |> buildOrderAggregateEventDispatcher

    EventsProcessor.init
    |> EventsProcessor.registerHandler (nameof RabbitMQ.OrderIntegrationEventDispatcher) eventDispatcher
    |> EventsProcessor.registerHandler "EventLogger" logEvent
    |> EventsProcessor.withErrorHandler logError
    |> EventsProcessor.build readEvents persistHandlers markEventsProcessed

let buildOrderAggregateEventsProcessorFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildOrderAggregateEventsProcessor


type OrderWorkflowIoError =
    | SqlIoError of SqlIoError
    | RabbitMQIoError of RabbitMQIoError
    | HttpIoError of HttpIoError

[<RequireQualifiedAccess>]
module private OrderWorkflows =
    let inline private buildTransactionalWorkflowExecutor (services: Services) buildWorkflow buildExecute =
        let sqlOrderAdapter = services.Get<ISqlOrderAggregateManagementAdapter>()
        let getUtcNow = services.Get<GetUtcNow>()
        let generateEventId = services.Get<GenerateId<eventId>>()

        let publishEvents =
            services.Get<OrderAggregateEventsProcessor>().Publish
            >>> AsyncResult.mapError OrderWorkflowIoError.RabbitMQIoError

        let inTransaction =
            fun dbTransaction ->
                let mapToSqlIoError e =
                    e |> AsyncResult.mapError OrderWorkflowIoError.SqlIoError

                let readOrder =
                    dbTransaction
                    |> SqlSession.Sustained
                    |> sqlOrderAdapter.ReadOrderAggregate
                    >> mapToSqlIoError

                let persistOrder =
                    dbTransaction |> sqlOrderAdapter.PersistOrderAggregate >>> mapToSqlIoError

                let persistEvents =
                    dbTransaction |> sqlOrderAdapter.PersistOrderAggregateEvents >>> mapToSqlIoError

                let execute =
                    buildExecute getUtcNow generateEventId readOrder persistOrder persistEvents

                let workflow = dbTransaction |> buildWorkflow

                execute workflow

        services
        |> getDbConnection
        |> TransactionalWorkflowExecutor.init
        |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
        |> TransactionalWorkflowExecutor.execute inTransaction OrderWorkflowIoError.SqlIoError
        |> TransactionalWorkflowExecutor.andThen publishEvents

    let inline private executeForNewAggregate (services: Services) buildWorkflow =
        fun command ->
            services.Get<GenerateAggregateId<Order.State>>()
            |> WorkflowExecutor.executeForNewAggregate Order.init command
            |> buildTransactionalWorkflowExecutor services buildWorkflow

    let inline private executeForExistingAggregate (services: Services) buildWorkflow =
        fun aggregateId command ->
            WorkflowExecutor.executeForExistingAggregate aggregateId command
            |> buildTransactionalWorkflowExecutor services buildWorkflow

    let inline private executeActionForExistingAggregate (services: Services) workflow =
        fun aggregateId ->
            WorkflowExecutor.executeActionForExistingAggregate aggregateId
            |> buildTransactionalWorkflowExecutor services (fun _ -> workflow)

    let buildStartOrder (services: Services) =
        let httpPaymentAdapter = services.Get<IHttpPaymentManagementAdapter>()
        let sqlPaymentAdapter = services.Get<ISqlPaymentManagementAdapter>()

        let verifyPaymentMethod =
            httpPaymentAdapter.VerifyPaymentMethod
            >> AsyncResult.mapError (Either.mapRight OrderWorkflowIoError.HttpIoError)

        fun dbTransaction ->
            let getSupportedCardTypes =
                dbTransaction
                |> SqlSession.Sustained
                |> sqlPaymentAdapter.GetSupportedCardTypes
                >> AsyncResult.mapError OrderWorkflowIoError.SqlIoError

            StartOrderWorkflow.build getSupportedCardTypes verifyPaymentMethod
        |> executeForNewAggregate services

    let buildAwaitOrderStockItemsValidation (services: Services) =
        AwaitOrderStockItemsValidationWorkflow.build
        |> executeActionForExistingAggregate services

    let buildConfirmOrderItemsStock (services: Services) =
        ConfirmOrderItemsStockWorkflow.build
        |> executeActionForExistingAggregate services

    let buildRejectOrderItemsStock (services: Services) =
        fun _ -> RejectOrderItemsStockWorkflow.build
        |> executeForExistingAggregate services

    let buildCancelOrder (services: Services) =
        CancelOrderWorkflow.build |> executeActionForExistingAggregate services

    let buildPayOrder (services: Services) =
        PayOrderWorkflow.build |> executeActionForExistingAggregate services

type EventDrivenOrderWorkflowDispatcherError =
    | WorkflowExecutorError of WorkflowExecutionError<Order.State, Order.InvalidStateError, OrderWorkflowIoError>
    | InvalidEventData of string

type EventDrivenOrderWorkflowDispatcher =
    EventHandler<Order.State, IntegrationEvent.Consumed, EventDrivenOrderWorkflowDispatcherError>

let private buildEventDrivenOrderWorkflow services : EventDrivenOrderWorkflowDispatcher =
    fun orderAggregateId integrationEvent ->
        let inline toWorkflowError x =
            x |> AsyncResult.mapError WorkflowExecutorError

        match integrationEvent.Data with
        | IntegrationEvent.Consumed.GracePeriodConfirmed _ ->
            orderAggregateId
            |> OrderWorkflows.buildAwaitOrderStockItemsValidation services
            |> toWorkflowError

        | IntegrationEvent.Consumed.OrderStockConfirmed _ ->
            orderAggregateId
            |> OrderWorkflows.buildConfirmOrderItemsStock services
            |> toWorkflowError

        | IntegrationEvent.Consumed.OrderStockRejected orderStockRejected ->
            asyncResult {
                let! command =
                    orderStockRejected.OrderStockItems
                    |> List.filter (_.HasStock >> not)
                    |> List.map (_.ProductId >> ProductId.ofInt)
                    |> NonEmptyList.ofList
                    |> Result.map (fun items ->
                        ({ RejectedOrderItems = items }: Order.Command.SetStockRejectedOrderStatus))
                    |> Result.setError (
                        $"Invalid event %s{integrationEvent.Id |> EventId.toString} of type %s{nameof IntegrationEvent.Consumed.OrderStockRejected}: no rejected stock found"
                        |> InvalidEventData
                    )

                return!
                    command
                    |> OrderWorkflows.buildRejectOrderItemsStock services orderAggregateId
                    |> toWorkflowError
            }

        | IntegrationEvent.Consumed.OrderPaymentFailed _ ->
            orderAggregateId |> OrderWorkflows.buildCancelOrder services |> toWorkflowError

        | IntegrationEvent.Consumed.OrderPaymentSucceeded _ ->
            orderAggregateId |> OrderWorkflows.buildPayOrder services |> toWorkflowError

type OrderIntegrationEventsProcessor =
    EventsProcessor<Order.State, IntegrationEvent.Consumed, SqlIoError, EventDrivenOrderWorkflowDispatcherError>

let private buildOrderIntegrationEventsProcessor services : OrderIntegrationEventsProcessor =
    let sqlSession = services |> getStandaloneSqlSession
    let config = services.Get<IOptions<Configuration.RabbitMQOptions>>()
    let logger = services.Get<ILogger<OrderIntegrationEventsProcessor>>()

    let orderIntegrationEventsAdapter =
        services.Get<ISqlOrderIntegrationEventsAdapter>()

    let readEvents =
        sqlSession
        |> orderIntegrationEventsAdapter.ReadUnprocessedOrderIntegrationEvents

    let persistHandlers =
        sqlSession
        |> orderIntegrationEventsAdapter.PersistSuccessfulOrderIntegrationEventHandlers

    let markEventsProcessed =
        sqlSession |> orderIntegrationEventsAdapter.MarkOrderIntegrationEventAsProcessed

    let retries =
        fun (attempt: int) -> TimeSpan.FromSeconds(Math.Pow(2, attempt))
        |> List.init config.Value.RetryCount

    let eventDrivenOrderWorkflow = services |> buildEventDrivenOrderWorkflow

    let logError = Logger.logEventsProcessorError logger

    let logEvent = Logger.logEvent logger

    EventsProcessor.init
    |> EventsProcessor.withRetries retries
    |> EventsProcessor.registerHandler (nameof EventDrivenOrderWorkflowDispatcher) eventDrivenOrderWorkflow
    |> EventsProcessor.registerHandler "EventLogger" logEvent
    |> EventsProcessor.withErrorHandler logError
    |> EventsProcessor.build readEvents persistHandlers markEventsProcessed

let buildOrderIntegrationEventsProcessorFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildOrderIntegrationEventsProcessor
