[<RequireQualifiedAccess>]
module eShop.Ordering.API.CompositionRoot

open System
open System.Data
open System.Data.Common
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open eShop.DomainDrivenDesign
open eShop.DomainDrivenDesign.Postgres
open eShop.Ordering.API.PortsAdapters
open eShop.Ordering.Adapters.Common
open eShop.Ordering.Adapters.Postgres
open eShop.Ordering.Domain.Model
open eShop.Ordering.Domain.Workflows
open eShop.Postgres
open eShop.RabbitMQ
open FsToolkit.ErrorHandling

type DI<'t> = DI of (unit -> 't) with

        static member inline GetService(getService) = getService()

let inline getServiceFromSp (sp: IServiceProvider) =
    DI.GetService<'t>(fun () -> sp.GetRequiredService<'t>())

// let inline getServiceFromCtx<'t> (ctx: HttpContext) =
//     DI.GetService<'t>(ctx.GetService<'t>, DI)

let inline private buildTransactionalWorkflowExecutor getService =
    let dbConnection = getService()

    dbConnection
    |> TransactionalWorkflowExecutor.init
    |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
    |> TransactionalWorkflowExecutor.execute

let inline private buildTransactionalWorkflowExecutorFromSp sp =
    sp |> getServiceFromSp |> buildTransactionalWorkflowExecutor

let inline buildOrderWorkflowExecutor getService workflowToExecute =
    let postgresAdapter = getService<IPostgresOrderAggregateManagementAdapter>

    let getUtcNow: GetUtcNow = getService<GetUtcNow>

    fun dbTransaction ->
        let readOrder =
            dbTransaction |> SqlSession.Sustained |> postgresAdapter.ReadOrderAggregate

        let persistOrder = dbTransaction |> postgresAdapter.PersistOrderAggregate

        let persistOrderEvents =
            dbTransaction |> postgresAdapter.PersistOrderAggregateEvents

        let publishOrderEvents = postgresAdapter.PublishOrderAggregateEvents

        workflowToExecute
        |> WorkflowExecutor.execute getUtcNow readOrder persistOrder persistOrderEvents publishOrderEvents
    |> buildTransactionalWorkflowExecutor sp



type OrderIntegrationEventDrivenWorkflowIoError = OrderIntegrationEventDrivenWorkflowIoError

type OrderIntegrationEventDrivenWorkflow =
    | AwaitOrderStockValidation of AwaitOrderStockItemsValidationWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | CancelOrder of CancelOrderWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | PayOrder of PayOrderWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | ConfirmOrderItemsStock of ConfirmOrderItemsStockWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | RejectOrderItemsStock of RejectOrderItemsStockWorkflow<OrderIntegrationEventDrivenWorkflowIoError>

type OrderIntegrationEventsProcessor =
    OrderIntegrationEventManagementAdapter.OrderIntegrationEventsProcessor<RabbitMQIoError>

let private buildOrderIntegrationEventDrivenWorkflows
    (sp: IServiceProvider)
    : EventHandler<
          OrderAggregate.State,
          Postgres.EventId,
          IntegrationEvent.Consumed,
          OrderIntegrationEventDrivenWorkflowIoError
       >
    =
    let workflowExecutor = buildTransactionalWorkflowExecutor sp

    fun orderAggregateId _ integrationEvent ->
        match integrationEvent.Data with
        | IntegrationEvent.Consumed.GracePeriodConfirmed gracePeriodConfirmed -> failwith "todo"
        | IntegrationEvent.Consumed.OrderStockConfirmed orderStockConfirmed -> failwith "todo"
        | IntegrationEvent.Consumed.OrderStockRejected orderStockRejected -> failwith "todo"
        | IntegrationEvent.Consumed.OrderPaymentFailed orderPaymentFailed -> failwith "todo"
        | IntegrationEvent.Consumed.OrderPaymentSucceeded orderPaymentSucceeded -> failwith "todo"


let buildOrderIntegrationEventsProcessor (sp: IServiceProvider) : OrderIntegrationEventsProcessor =
    let sqlSession = sp |> getStandaloneSqlSession
    let config = sp |> getServiceFromSp<IOptions<Configuration.RabbitMQOptions>>

    let postgresAdapter =
        sp |> getServiceFromSp<IPostgresOrderIntegrationEventsProcessorAdapter>

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

let getOrderIntegrationEventsProcessor =
    getServiceFromSp<OrderIntegrationEventsProcessor>

// [<RequireQualifiedAccess>]
// module OrderWorkflowExecutor =
//     let build (ctx)

// let private buildTransactionalWorkflowExecutor =
//
