[<RequireQualifiedAccess>]
module eShop.Ordering.API.CompositionRoot

open System
open System.Data
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open RabbitMQ.Client
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

let private buildTransactionalWorkflowExecutor (services: Services) =
    services
    |> getDbConnection
    |> TransactionalWorkflowExecutor.init
    |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
    |> TransactionalWorkflowExecutor.execute

let private buildOrderWorkflowExecutor (services: Services) workflowToExecute =
    let postgresAdapter = services.Get<IPostgresOrderAggregateManagementAdapter>()

    let getUtcNow = services.Get<GetUtcNow>()

    fun dbTransaction ->
        let readOrder =
            dbTransaction |> SqlSession.Sustained |> postgresAdapter.ReadOrderAggregate

        let persistOrder = dbTransaction |> postgresAdapter.PersistOrderAggregate

        let persistOrderEvents =
            dbTransaction |> postgresAdapter.PersistOrderAggregateEvents

        let publishOrderEvents = postgresAdapter.PublishOrderAggregateEvents

        workflowToExecute
        |> WorkflowExecutor.execute getUtcNow readOrder persistOrder persistOrderEvents publishOrderEvents
    |> buildTransactionalWorkflowExecutor services

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

    let postgresAdapter = services.Get<IPostgresOrderAggregateEventsProcessorAdapter>()

    let readEvents = sqlSession |> postgresAdapter.ReadUnprocessedOrderEvents
    let persistHandlers = sqlSession |> postgresAdapter.PersistSuccessfulEventHandlers
    let markEventsProcessed = sqlSession |> postgresAdapter.MarkEventAsProcessed

    let integrationEventDispatcher =
        services |> buildOrderAggregateIntegrationEventDispatcher

    EventsProcessor.init
    |> EventsProcessor.registerHandler "IntegrationEventDispatcher" integrationEventDispatcher
    |> EventsProcessor.build readEvents persistHandlers markEventsProcessed

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
          OrderIntegrationEventDrivenWorkflowIoError
       >
    =
    let workflowExecutor = services |> buildTransactionalWorkflowExecutor

    fun orderAggregateId _ integrationEvent ->
        match integrationEvent.Data with
        | IntegrationEvent.Consumed.GracePeriodConfirmed gracePeriodConfirmed -> failwith "todo"
        | IntegrationEvent.Consumed.OrderStockConfirmed orderStockConfirmed -> failwith "todo"
        | IntegrationEvent.Consumed.OrderStockRejected orderStockRejected -> failwith "todo"
        | IntegrationEvent.Consumed.OrderPaymentFailed orderPaymentFailed -> failwith "todo"
        | IntegrationEvent.Consumed.OrderPaymentSucceeded orderPaymentSucceeded -> failwith "todo"

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

// [<RequireQualifiedAccess>]
// module OrderWorkflowExecutor =
//     let build (ctx)

// let private buildTransactionalWorkflowExecutor =
//
