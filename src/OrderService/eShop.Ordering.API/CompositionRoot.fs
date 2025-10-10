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
type internal Services =
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
        taskResult {
            let persistIntegrationEvents =
                services.Get<ISqlOrderIntegrationEventsAdapter>().PersistOrderIntegrationEvents

            let connection = (services |> getDbConnection) ()

            do! connection.OpenAsync()

            let! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted).AsTask()

            return!
                persistIntegrationEvents transaction aggregateId events
                |> TaskResult.teeAsync (fun _ -> transaction.CommitAsync() |> Task.ofUnit)
                |> TaskResult.teeErrorAsync (fun _ -> transaction.RollbackAsync() |> Task.ofUnit)
                |> TaskResult.teeAnyAsync (connection.CloseAsync >> Task.ofUnit)
        }

let buildPersistIntegrationEventsFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildPersistIntegrationEvents


type OrderAggregateEventsProcessor = EventsProcessor<Order.State, Order.Event, SqlIoError, RabbitMQIoError>

[<RequireQualifiedAccess>]
module OrderAggregateEventsProcessor =
    let private build services : OrderAggregateEventsProcessor =
        let sqlSession = services |> getStandaloneSqlSession
        let logger = services.Get<ILogger<OrderAggregateEventsProcessor>>()
        let rabbitMQConnection = services.Get<IConnection>()
        let jsonOptions = services.Get<JsonSerializerOptions>()

        let orderAggregateAdapter = services.Get<ISqlOrderAggregateManagementAdapter>()

        let readEvents =
            sqlSession |> orderAggregateAdapter.ReadUnprocessedOrderAggregateEvents

        let persistHandlers =
            sqlSession |> orderAggregateAdapter.PersistSuccessfulOrderAggregateEventHandlers

        let markEventsProcessed =
            sqlSession |> orderAggregateAdapter.MarkOrderAggregateEventAsProcessed

        let logError = Logger.logEventsProcessorError logger

        let logEvent = Logger.logEvent logger

        let dispatchOrderIntegrationEvent =
            fun orderAggregateId orderAggregateEvent ->
                orderAggregateEvent
                |> Event.mapPayload (IntegrationEvent.Published.ofAggregateEvent orderAggregateId)
                |> RabbitMQ.OrderIntegrationEventDispatcher.create jsonOptions rabbitMQConnection

        EventsProcessor.init
        |> EventsProcessor.registerHandler
            (nameof RabbitMQ.OrderIntegrationEventDispatcher)
            dispatchOrderIntegrationEvent
        |> EventsProcessor.registerHandler "EventLogger" logEvent
        |> EventsProcessor.withErrorHandler logError
        |> EventsProcessor.build readEvents persistHandlers markEventsProcessed

    let buildFromSp (sp: IServiceProvider) = sp |> Services.fromSp |> build


[<RequireQualifiedAccess>]
module OrderWorkflowExecutor =
    let execute (services: Services) mapIoError workflow =
        let getUtcNow = services.Get<GetUtcNow>()
        let generateEventId = services.Get<GenerateEventId>()

        let sqlOrderAdapter = services.Get<ISqlOrderAggregateManagementAdapter>()

        let toSqlIoError e = e |> TaskResult.mapError mapIoError

        let readOrder =
            SqlSession.Sustained >> sqlOrderAdapter.ReadOrderAggregate >>> toSqlIoError

        let persistOrder = sqlOrderAdapter.PersistOrderAggregate >>>> toSqlIoError
        let persistEvents = sqlOrderAdapter.PersistOrderAggregateEvents >>>> toSqlIoError

        services
        |> getDbConnection
        |> TransactionalWorkflowExecutor.init
        |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
        |> TransactionalWorkflowExecutor.withIoErrorMapping mapIoError
        |> TransactionalWorkflowExecutor.execute getUtcNow generateEventId readOrder persistOrder persistEvents workflow

    let executeNew (services: Services) mapIoError workflow =
        let id = services.Get<GenerateAggregateId<Order.State>> () ()

        id |> execute services mapIoError workflow |> TaskResult.map (fun x -> id, x)

type OrderWorkflowIoError =
    | SqlIoError of SqlIoError
    | RabbitMQIoError of RabbitMQIoError
    | HttpIoError of HttpIoError

[<RequireQualifiedAccess>]
module OrderWorkflow =
    let private buildStartOrder (services: Services) =
        let httpPaymentAdapter = services.Get<IHttpPaymentManagementAdapter>()
        let sqlPaymentAdapter = services.Get<ISqlPaymentManagementAdapter>()

        let verifyPaymentMethod =
            httpPaymentAdapter.VerifyPaymentMethod
            >> TaskResult.mapError (Either.mapRight OrderWorkflowIoError.HttpIoError)

        reader {
            let! getSupportedCardTypes =
                SqlSession.Sustained
                >> sqlPaymentAdapter.GetSupportedCardTypes
                >>> TaskResult.mapError OrderWorkflowIoError.SqlIoError

            StartOrderWorkflow.build getSupportedCardTypes verifyPaymentMethod
        }
        |> OrderWorkflowExecutor.executeNew services OrderWorkflowIoError.SqlIoError

    let buildStartOrderFromCtx ctx =
        ctx |> Services.fromCtx |> buildStartOrder

    let internal buildAwaitOrderStockItemsValidation (services: Services) =
        AwaitOrderStockItemsValidationWorkflow.build
        |> executeActionForExistingAggregate services

    let internal buildConfirmOrderItemsStock (services: Services) =
        ConfirmOrderItemsStockWorkflow.build
        |> executeActionForExistingAggregate services

    let internal buildRejectOrderItemsStock (services: Services) =
        fun _ -> RejectOrderItemsStockWorkflow.build
        |> executeForExistingAggregate services

    let internal buildCancelOrder (services: Services) =
        CancelOrderWorkflow.build |> executeActionForExistingAggregate services

    let internal buildPayOrder (services: Services) =
        PayOrderWorkflow.build |> executeActionForExistingAggregate services


type EventDrivenOrderWorkflowDispatcherError =
    | WorkflowExecutorError of WorkflowExecutionError<Order.State, Order.InvalidStateError, OrderWorkflowIoError>
    | InvalidEventData of string

type EventDrivenOrderWorkflowDispatcher =
    EventHandler<Order.State, IntegrationEvent.Consumed, EventDrivenOrderWorkflowDispatcherError>

type OrderIntegrationEventsProcessor =
    EventsProcessor<Order.State, IntegrationEvent.Consumed, SqlIoError, EventDrivenOrderWorkflowDispatcherError>

[<RequireQualifiedAccess>]
module OrderIntegrationEventsProcessor =
    let private dispatchEventDrivenWorkflow services : EventDrivenOrderWorkflowDispatcher =
        fun orderAggregateId integrationEvent ->
            let inline toWorkflowError x =
                x |> AsyncResult.mapError WorkflowExecutorError

            match integrationEvent.Data with
            | IntegrationEvent.Consumed.GracePeriodConfirmed _ ->
                orderAggregateId
                |> OrderWorkflow.buildAwaitOrderStockItemsValidation services
                |> toWorkflowError

            | IntegrationEvent.Consumed.OrderStockConfirmed _ ->
                orderAggregateId
                |> OrderWorkflow.buildConfirmOrderItemsStock services
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
                        |> OrderWorkflow.buildRejectOrderItemsStock services orderAggregateId
                        |> toWorkflowError
                }

            | IntegrationEvent.Consumed.OrderPaymentFailed _ ->
                orderAggregateId |> OrderWorkflow.buildCancelOrder services |> toWorkflowError

            | IntegrationEvent.Consumed.OrderPaymentSucceeded _ ->
                orderAggregateId |> OrderWorkflow.buildPayOrder services |> toWorkflowError

    let private build services : OrderIntegrationEventsProcessor =
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

        let eventDrivenOrderWorkflow = services |> dispatchEventDrivenWorkflow

        let logError = Logger.logEventsProcessorError logger

        let logEvent = Logger.logEvent logger

        EventsProcessor.init
        |> EventsProcessor.withRetries retries
        |> EventsProcessor.registerHandler (nameof EventDrivenOrderWorkflowDispatcher) eventDrivenOrderWorkflow
        |> EventsProcessor.registerHandler "EventLogger" logEvent
        |> EventsProcessor.withErrorHandler logError
        |> EventsProcessor.build readEvents persistHandlers markEventsProcessed

    let buildFromSp (sp: IServiceProvider) = sp |> Services.fromSp |> build
