[<RequireQualifiedAccess>]
module eShop.Ordering.API.CompositionRoot

open System
open System.Data
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

let private getService<'t> (sp: IServiceProvider) = sp.GetRequiredService<'t>()

let private getDbConnection = getService<GetDbConnection>

let private getStandaloneSqlSession = getDbConnection >> SqlSession.Standalone

let buildTransactionalWorkflowExecutor (sp: IServiceProvider) =
    getDbConnection sp
    |> TransactionalWorkflowExecutor.init
    |> TransactionalWorkflowExecutor.withIsolationLevel IsolationLevel.ReadCommitted
    |> TransactionalWorkflowExecutor.execute

type OrderIntegrationEventDrivenWorkflowIoError = OrderIntegrationEventDrivenWorkflowIoError

type OrderIntegrationEventDrivenWorkflow =
    | AwaitOrderStockValidation of AwaitOrderStockItemsValidationWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | CancelOrder of CancelOrderWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | PayOrder of PayOrderWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | ConfirmOrderItemsStock of ConfirmOrderItemsStockWorkflow<OrderIntegrationEventDrivenWorkflowIoError>
    | RejectOrderItemsStock of RejectOrderItemsStockWorkflow<OrderIntegrationEventDrivenWorkflowIoError>

type OrderIntegrationEventsProcessor =
    PostgresOrderIntegrationEventManagementAdapter.OrderIntegrationEventsProcessor<RabbitMQIoError>

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
    let config = sp |> getService<IOptions<Configuration.RabbitMQOptions>>

    let postgresAdapter =
        sp |> getService<IPostgresOrderIntegrationEventsProcessorAdapter>

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

let getOrderIntegrationEventsProcessor = getService<OrderIntegrationEventsProcessor>

// [<RequireQualifiedAccess>]
// module OrderWorkflowExecutor =
//     let build (ctx)

// let private buildTransactionalWorkflowExecutor =
//
