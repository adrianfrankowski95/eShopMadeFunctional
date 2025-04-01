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
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Workflows
open eShop.Postgres
open eShop.RabbitMQ
open FsToolkit.ErrorHandling
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

let private buildTransactionalWorkflowExecutor (services: Services) =
    services
    |> getDbConnection
    |> TransactionalWorkflowExecutor.init
    |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
    |> TransactionalWorkflowExecutor.execute

type OrderAggregateIntegrationEventDispatcher = RabbitMQ.OrderAggregateIntegrationEventDispatcher<Postgres.EventId>

let private buildOrderAggregateIntegrationEventDispatcher
    (services: Services)
    : OrderAggregateIntegrationEventDispatcher =
    let rabbitMQConnection = services.Get<IConnection>()

    let jsonOptions = services.Get<JsonSerializerOptions>()

    RabbitMQ.OrderAggregateIntegrationEventDispatcher.create jsonOptions rabbitMQConnection

type OrderAggregateEventsProcessor = Postgres.OrderAdapter.OrderAggregateEventsProcessor<RabbitMQIoError>

let private buildOrderAggregateEventsProcessor services : OrderAggregateEventsProcessor =
    let sqlSession = services |> getStandaloneSqlSession
    let logger = services.Get<ILogger<OrderAggregateEventsProcessor>>()
    let postgresAdapter = services.Get<IPostgresOrderAggregateEventsProcessorAdapter>()

    let readEvents = sqlSession |> postgresAdapter.ReadUnprocessedOrderEvents
    let persistHandlers = sqlSession |> postgresAdapter.PersistSuccessfulEventHandlers
    let markEventsProcessed = sqlSession |> postgresAdapter.MarkEventAsProcessed

    let integrationEventDispatcher =
        services |> buildOrderAggregateIntegrationEventDispatcher

    let logError (error: EventsProcessor.EventsProcessorError<'state, _, _, SqlIoError, _>) =
        match error with
        | EventsProcessor.ReadingUnprocessedEventsFailed eventLogIoError ->
            logger.LogError("Reading unprocessed events failed: {Error}", eventLogIoError)

        | EventsProcessor.PersistingSuccessfulEventHandlersFailed(AggregateId aggregateId,
                                                                  Postgres.EventId eventId,
                                                                  successfulHandlers,
                                                                  eventLogIoError) ->
            logger.LogError(
                "Persisting successful event handlers ({SuccessfulEventHandlers}) for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) failed: {Error}",
                (successfulHandlers |> String.concat ", "),
                eventId,
                Aggregate.typeName<'state>,
                aggregateId,
                eventLogIoError
            )

        | EventsProcessor.MarkingEventAsProcessedFailed(Postgres.EventId eventId, eventLogIoError) ->
            logger.LogError("Marking Event {EventId} as processed failed: {Error}", eventId, eventLogIoError)

        | EventsProcessor.EventHandlerFailed(attempt,
                                             AggregateId aggregateId,
                                             Postgres.EventId eventId,
                                             event,
                                             handlerName,
                                             eventHandlingIoError) ->
            logger.LogError(
                "Handler {HandlerName} for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) failed after #{Attempt} attempt. Event data: {Event}, error: {Error}",
                handlerName,
                eventId,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                event,
                eventHandlingIoError
            )

        | EventsProcessor.MaxEventProcessingRetriesReached(attempt,
                                                           AggregateId aggregateId,
                                                           Postgres.EventId eventId,
                                                           event,
                                                           handlerNames) ->
            logger.LogCritical(
                "Handlers {HandlerNames} for Event {EventId} (Aggregate {AggregateType} - {AggregateId}) reached maximum attempts ({attempt}) and won't be retried. Event data: {Event}",
                (handlerNames |> String.concat ", "),
                eventId,
                Aggregate.typeName<'state>,
                aggregateId,
                attempt,
                event
            )

    EventsProcessor.init
    |> EventsProcessor.registerHandler "IntegrationEventDispatcher" integrationEventDispatcher
    |> EventsProcessor.withErrorHandler logError
    |> EventsProcessor.build readEvents persistHandlers markEventsProcessed

let buildOrderAggregateEventsProcessorFromSp (sp: IServiceProvider) =
    sp |> Services.fromSp |> buildOrderAggregateEventsProcessor

let private buildOrderWorkflowExecutor (services: Services) workflowToExecute =
    let postgresAdapter = services.Get<IPostgresOrderAggregateManagementAdapter>()

    let getUtcNow = services.Get<GetUtcNow>()

    let eventsProcessor = services.Get<OrderAggregateEventsProcessor>()

    fun dbTransaction ->
        let readOrder =
            dbTransaction |> SqlSession.Sustained |> postgresAdapter.ReadOrderAggregate

        let persistOrder = dbTransaction |> postgresAdapter.PersistOrderAggregate

        let persistOrderEvents =
            dbTransaction |> postgresAdapter.PersistOrderAggregateEvents

        let publishOrderEvents = eventsProcessor.Process >>> AsyncResult.ok

        workflowToExecute
        |> WorkflowExecutor.execute getUtcNow readOrder persistOrder persistOrderEvents publishOrderEvents
    |> buildTransactionalWorkflowExecutor services



type OrderIntegrationEventDrivenWorkflowIoError = OrderIntegrationEventDrivenWorkflowIoError

type OrderIntegrationEventDrivenWorkflow =
    | AwaitOrderStockValidation of AwaitOrderStockItemsValidationWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | CancelOrder of CancelOrderWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | PayOrder of PayOrderWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | ConfirmOrderItemsStock of ConfirmOrderItemsStockWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | RejectOrderItemsStock of RejectOrderItemsStockWorkflow<OrderIntegrationEventDrivenWorkflowIoError>

let private buildOrderIntegrationEventDrivenWorkflows
    services
    : EventHandler<
          OrderAggregate.State,
          Postgres.EventId,
          IntegrationEvent.Consumed,
          WorkflowExecutorError<OrderAggregate.InvalidStateError, _, SqlIoError, _>
       >
    =

    fun orderAggregateId _ integrationEvent ->
        let inline executeWorkflow command workflow = buildOrderWorkflowExecutor services workflow orderAggregateId command

        
        match integrationEvent.Data with
        | IntegrationEvent.Consumed.GracePeriodConfirmed _ ->
            AwaitOrderStockItemsValidationWorkflow.build |> executeWorkflow ()

        | IntegrationEvent.Consumed.OrderStockConfirmed _ ->
            ConfirmOrderItemsStockWorkflow.build |> executeWorkflow ()

        | IntegrationEvent.Consumed.OrderStockRejected orderStockRejected ->
            asyncResult {
                let! command =
                    orderStockRejected.OrderStockItems
                    |> List.filter (_.HasStock >> not)
                    |> List.map (_.ProductId >> ProductId.ofInt)
                    |> NonEmptyList.ofList
                    |> Result.mapError ((+) "OrderStockRejected error: ")
                    |> Result.map (fun items -> ({ RejectedOrderItems = items }: OrderAggregate.Command.SetStockRejectedOrderStatus))
                
                return! RejectOrderItemsStockWorkflow.build |> executeWorkflow command
            }
            
        
        | IntegrationEvent.Consumed.OrderPaymentFailed orderPaymentFailed ->
            asyncResult
                {

                }
        | IntegrationEvent.Consumed.OrderPaymentSucceeded orderPaymentSucceeded ->
            asyncResult
                {

                }

type OrderIntegrationEventsProcessor = Postgres.OrderAdapter.OrderIntegrationEventsProcessor<RabbitMQIoError>

let private buildOrderIntegrationEventsProcessor services : OrderIntegrationEventsProcessor =
    let sqlSession = services |> getStandaloneSqlSession
    let config = services.Get<IOptions<Configuration.RabbitMQOptions>>()

    let postgresAdapter =
        services.Get<IPostgresOrderIntegrationEventsProcessorAdapter>()

    let readEvents = sqlSession |> postgresAdapter.ReadUnprocessedOrderEvents
    let persistHandlers = sqlSession |> postgresAdapter.PersistSuccessfulEventHandlers
    let markEventsProcessed = sqlSession |> postgresAdapter.MarkEventAsProcessed

    let retries =
        (fun (attempt: int) -> TimeSpan.FromSeconds(Math.Pow(2, attempt)))
        |> List.init config.Value.RetryCount

    EventsProcessor.init
    |> EventsProcessor.withRetries retries
    |> EventsProcessor.registerHandler
        "Dummy Handler"
        ((fun _ _ _ -> AsyncResult.ok ()): EventHandler<_, _, _, RabbitMQIoError>)
    |> EventsProcessor.build readEvents persistHandlers markEventsProcessed
