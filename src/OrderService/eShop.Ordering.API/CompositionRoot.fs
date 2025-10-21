[<RequireQualifiedAccess>]
module eShop.Ordering.API.CompositionRoot

open System
open System.Transactions
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open RabbitMQ.Client
open eShop.DomainDrivenDesign
open eShop.Ordering.API.PortsAdapters
open eShop.Ordering.Adapters
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Adapters.Common.IntegrationEvent
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
            member this.Get<'t>() = sp.GetRequiredService<'t>() }

    let fromCtx (ctx: HttpContext) =
        { new Services with
            member this.Get<'t>() = ctx.GetService<'t>() }


let private getDbConnection (services: Services) = services.Get<GetDbConnection>()

let private buildPersistIntegrationEvents (services: Services) : PersistEvents<_, _, _> =
    fun aggregateId events ->
        let orderIntegrationEventsAdapter =
            services.Get<ISqlOrderIntegrationEventsAdapter>()

        services
        |> getDbConnection
        |> TransactionalExecutor.init
        |> TransactionalExecutor.withIsolationLevel IsolationLevel.ReadCommitted
        |> TransactionalExecutor.withRetries []
        |> TransactionalExecutor.execute
        <| fun conn -> orderIntegrationEventsAdapter.PersistOrderIntegrationEvents conn aggregateId events

let buildPersistIntegrationEventsFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildPersistIntegrationEvents


type OrderAggregateEventsProcessor = EventsProcessor<Order.State, Order.Event, SqlIoError, RabbitMQIoError>

[<RequireQualifiedAccess>]
module OrderAggregateEventsProcessor =
    let private build services : OrderAggregateEventsProcessor =
        let dbConnection = services |> getDbConnection
        let logger = services.Get<ILogger<OrderAggregateEventsProcessor>>()
        let rabbitMQConnection = services.Get<IConnection>()
        let jsonOptions = services.Get<JsonSerializerOptions>()

        let orderAggregateAdapter = services.Get<ISqlOrderAggregateManagementAdapter>()

        let readEvents =
            dbConnection |> orderAggregateAdapter.ReadUnprocessedOrderAggregateEvents

        let persistHandlers =
            dbConnection
            |> orderAggregateAdapter.PersistSuccessfulOrderAggregateEventHandlers

        let markEventsProcessed =
            dbConnection |> orderAggregateAdapter.MarkOrderAggregateEventAsProcessed

        let logError = Logger.logEventsProcessorError logger

        let logEvent = Logger.logEvent logger

        let dispatchOrderIntegrationEvent =
            fun orderAggregateId orderAggregateEvent ->
                orderAggregateEvent
                |> Event.mapPayload (Published.ofAggregateEvent orderAggregateId)
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
        fun aggregateId ->
            let toIoError x = x |> TaskResult.mapError mapIoError
            
            let getUtcNow = services.Get<GetUtcNow>()
            let generateEventId = services.Get<GenerateEventId>()
            let sqlOrderAdapter = services.Get<ISqlOrderAggregateManagementAdapter>()

            services
            |> getDbConnection
            |> TransactionalExecutor.init
            |> TransactionalExecutor.withIsolationLevel IsolationLevel.ReadCommitted
            |> TransactionalExecutor.execute
            <| reader {
                let! readOrder = sqlOrderAdapter.ReadOrderAggregate >>> toIoError
                let! persistOrder = sqlOrderAdapter.PersistOrderAggregate >>>> toIoError
                let! persistEvents = sqlOrderAdapter.PersistOrderAggregateEvents >>>> toIoError
                let! workflow = workflow

                return
                    WorkflowExecutor.execute
                        aggregateId
                        getUtcNow
                        generateEventId
                        readOrder
                        persistOrder
                        persistEvents
                        workflow
            }

    let internal executeNew (services: Services) mapIoError workflow =
        let id = services.Get<GenerateAggregateId<Order.State>> () ()

        id |> execute services mapIoError workflow |> TaskResult.map (fun x -> id, x)

type OrderWorkflowIoError =
    | SqlIoError of SqlIoError
    | RabbitMQIoError of RabbitMQIoError
    | HttpIoError of HttpIoError

[<RequireQualifiedAccess>]
module OrderWorkflow =
    let private buildStartOrder (services: Services) command =
        let getUtcNow = services.Get<GetUtcNow>()
        let httpPaymentAdapter = services.Get<IHttpPaymentManagementAdapter>()
        let sqlPaymentAdapter = services.Get<ISqlPaymentManagementAdapter>()

        let verifyPaymentMethod =
            httpPaymentAdapter.VerifyPaymentMethod
            >> TaskResult.mapError OrderWorkflowIoError.HttpIoError

        reader {
            let! getSupportedCardTypes =
                sqlPaymentAdapter.GetSupportedCardTypes
                >>> TaskResult.mapError OrderWorkflowIoError.SqlIoError

            return StartOrderWorkflow.build getUtcNow getSupportedCardTypes verifyPaymentMethod command
        }
        |> OrderWorkflowExecutor.executeNew services OrderWorkflowIoError.SqlIoError

    let buildStartOrderFromCtx ctx =
        ctx |> Services.fromCtx |> buildStartOrder

    let internal buildAwaitOrderStockItemsValidation (services: Services) =
        Reader.retn AwaitOrderStockItemsValidationWorkflow.build
        |> OrderWorkflowExecutor.execute services OrderWorkflowIoError.SqlIoError

    let internal buildConfirmOrderItemsStock (services: Services) =
        Reader.retn ConfirmOrderItemsStockWorkflow.build
        |> OrderWorkflowExecutor.execute services OrderWorkflowIoError.SqlIoError

    let internal buildRejectOrderItemsStock (services: Services) command =
        Reader.retn (RejectOrderItemsStockWorkflow.build command)
        |> OrderWorkflowExecutor.execute services OrderWorkflowIoError.SqlIoError

    let internal buildCancelOrder (services: Services) =
        Reader.retn CancelOrderWorkflow.build
        |> OrderWorkflowExecutor.execute services OrderWorkflowIoError.SqlIoError

    let internal buildPayOrder (services: Services) =
        Reader.retn PayOrderWorkflow.build
        |> OrderWorkflowExecutor.execute services OrderWorkflowIoError.SqlIoError

type EventDrivenOrderWorkflowDispatcherError =
    | WorkflowExecutionError of Either<Order.InvalidStateError, OrderWorkflowIoError>
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
                x |> TaskResult.mapError WorkflowExecutionError

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
                taskResult {
                    let! command =
                        orderStockRejected
                        |> OrderStockRejected.toDomain
                        |> Result.mapError InvalidEventData

                    return!
                        orderAggregateId
                        |> OrderWorkflow.buildRejectOrderItemsStock services command
                        |> toWorkflowError
                }

            | IntegrationEvent.Consumed.OrderPaymentFailed _ ->
                orderAggregateId |> OrderWorkflow.buildCancelOrder services |> toWorkflowError

            | IntegrationEvent.Consumed.OrderPaymentSucceeded _ ->
                orderAggregateId |> OrderWorkflow.buildPayOrder services |> toWorkflowError

    let private build services : OrderIntegrationEventsProcessor =
        let dbConnection = services |> getDbConnection
        let config = services.Get<IOptions<Configuration.RabbitMQOptions>>()
        let logger = services.Get<ILogger<OrderIntegrationEventsProcessor>>()

        let orderIntegrationEventsAdapter =
            services.Get<ISqlOrderIntegrationEventsAdapter>()

        let readEvents =
            dbConnection
            |> orderIntegrationEventsAdapter.ReadUnprocessedOrderIntegrationEvents

        let persistHandlers =
            dbConnection
            |> orderIntegrationEventsAdapter.PersistSuccessfulOrderIntegrationEventHandlers

        let markEventsProcessed =
            dbConnection
            |> orderIntegrationEventsAdapter.MarkOrderIntegrationEventAsProcessed

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
