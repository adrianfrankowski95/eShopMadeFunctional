namespace eShop.DomainDrivenDesign

open System
open eShop.ConstrainedTypes
open eShop.Prelude
open FsToolkit.ErrorHandling

[<Measure>]
type aggregate

type AggregateId<'state> = private AggregateId of Id<aggregate>

type GenerateAggregateId<'state> = unit -> AggregateId<'state>

[<RequireQualifiedAccess>]
module AggregateId =
    let ofGuid<'state> : Guid -> AggregateId<'state> = Id.ofGuid >> AggregateId

    let value (AggregateId rawId) = rawId |> Id.value

    let generate<'state> : GenerateAggregateId<'state> =
        fun () -> Id.generate<aggregate> () |> AggregateId

[<AutoOpen>]
module ActivePatterns =
    let (|AggregateId|) = AggregateId.value

type Evolve<'state, 'command, 'event, 'stateTransitionError> =
    'command -> 'state -> Result<'state * 'event list, 'stateTransitionError>

type AggregateAction<'state, 'event, 'stateTransitionError> =
    'state -> Result<'state * 'event list, 'stateTransitionError>

[<RequireQualifiedAccess>]
module Aggregate =
    let typeName<'state> =
        let stateType = typeof<'state>

        stateType.DeclaringType.Name + stateType.Name

type Port<'input, 'output, 'ioError> = 'input -> AsyncResult<'output, 'ioError>

type AggregateOperation<'st, 'ev, 'err, 'retn> =
    private | AggregateOperation of ('st -> AsyncResult<'st * 'ev list * 'retn, 'err>)

[<RequireQualifiedAccess>]
module AggregateOperation =
    let internal run (AggregateOperation op) st = op st

    let retn x =
        fun st0 -> AsyncResult.ok (st0, [], x)
        |> AggregateOperation

    let bind
        (f: 'a -> AggregateOperation<'st, 'ev, 'err, 'b>)
        (a: AggregateOperation<'st, 'ev, 'err, 'a>)
        =
        fun st0 ->
            asyncResult {
                let! st1, ev1, a = run a st0
                let! st2, ev2, b = run (f a) st1

                return st2, ev1 @ ev2, b
            }
        |> AggregateOperation

    let combine
        (a: AggregateOperation<'state, 'event, 'error, 'a>)
        (b: AggregateOperation<'state, 'event, 'error, 'b>)
        =
        bind (fun _ -> b) a

// // 1
// module Domain.Workflow
//
// open System
//
// // Core domain types
// type AggregateId = AggregateId of Guid
// type EventMetadata = {
//     AggregateId: AggregateId
//     Timestamp: DateTimeOffset
//     Version: int
//     CorrelationId: Guid
// }
//
// // Base types for commands and events
// type ICommand = interface end
// type IDomainEvent = interface end
//
// // Result type for command execution
// type CommandResult<'State, 'Event> = {
//     State: 'State
//     Events: 'Event list
//     Metadata: EventMetadata list
// }
//
// // Port abstraction for IO operations
// type Port<'a, 'b> = 'a -> Async<Result<'b, string>>
//
// // The Workflow monad
// type Workflow<'State, 'Event, 'a> =
//     | Workflow of (('State * 'Event list) -> Async<Result<('a * 'State * 'Event list), string>>)
//
// // Workflow computation expression builder
// type WorkflowBuilder() =
//     member _.Return(x) : Workflow<'s, 'e, 'a> =
//         Workflow (fun (state, events) -> async { return Ok (x, state, events) })
//
//     member _.ReturnFrom(workflow: Workflow<'s, 'e, 'a>) : Workflow<'s, 'e, 'a> =
//         workflow
//
//     member _.Zero() : Workflow<'s, 'e, unit> =
//         Workflow (fun (state, events) -> async { return Ok ((), state, events) })
//
//     member _.Bind(Workflow f, g: 'a -> Workflow<'s, 'e, 'b>) : Workflow<'s, 'e, 'b> =
//         Workflow (fun (state, events) -> async {
//             match! f (state, events) with
//             | Ok (a, state', events') ->
//                 let (Workflow h) = g a
//                 return! h (state', events')
//             | Error e ->
//                 return Error e
//         })
//
//     member _.Combine(Workflow f, Workflow g) : Workflow<'s, 'e, 'b> =
//         Workflow (fun (state, events) -> async {
//             match! f (state, events) with
//             | Ok (_, state', events') -> return! g (state', events')
//             | Error e -> return Error e
//         })
//
//     member _.Delay(f: unit -> Workflow<'s, 'e, 'a>) : Workflow<'s, 'e, 'a> =
//         f()
//
//     member _.TryWith(Workflow f, handler: exn -> Workflow<'s, 'e, 'a>) : Workflow<'s, 'e, 'a> =
//         Workflow (fun (state, events) -> async {
//             try
//                 return! f (state, events)
//             with
//             | ex ->
//                 let (Workflow h) = handler ex
//                 return! h (state, events)
//         })
//
//     member _.TryFinally(Workflow f, compensation: unit -> unit) : Workflow<'s, 'e, 'a> =
//         Workflow (fun (state, events) -> async {
//             try
//                 return! f (state, events)
//             finally
//                 compensation()
//         })
//
// // Create the workflow computation expression
// let workflow = WorkflowBuilder()
//
// // Core workflow operations
// module Workflow =
//     // Run a workflow
//     let run (initialState: 'State) (Workflow f) : Async<Result<('a * CommandResult<'State, 'Event>), string>> =
//         async {
//             match! f (initialState, []) with
//             | Ok (result, finalState, events) ->
//                 return Ok (result, { State = finalState; Events = events; Metadata = [] })
//             | Error e ->
//                 return Error e
//         }
//
//     // Get current state
//     let getState<'State, 'Event> : Workflow<'State, 'Event, 'State> =
//         Workflow (fun (state, events) -> async { return Ok (state, state, events) })
//
//     // Set state
//     let setState (newState: 'State) : Workflow<'State, 'Event, unit> =
//         Workflow (fun (_, events) -> async { return Ok ((), newState, events) })
//
//     // Emit an event
//     let emit (event: 'Event) : Workflow<'State, 'Event, unit> =
//         Workflow (fun (state, events) -> async { return Ok ((), state, events @ [event]) })
//
//     // Emit multiple events
//     let emitMany (newEvents: 'Event list) : Workflow<'State, 'Event, unit> =
//         Workflow (fun (state, events) -> async { return Ok ((), state, events @ newEvents) })
//
//     // Execute a command
//     let execute (handler: 'State -> 'Cmd -> Result<('State * 'Event list), string>)
//                 (cmd: 'Cmd) : Workflow<'State, 'Event, unit> =
//         Workflow (fun (state, events) -> async {
//             match handler state cmd with
//             | Ok (newState, newEvents) ->
//                 return Ok ((), newState, events @ newEvents)
//             | Error e ->
//                 return Error e
//         })
//
//     // Lift an async computation
//     let liftAsync (computation: Async<'a>) : Workflow<'State, 'Event, 'a> =
//         Workflow (fun (state, events) -> async {
//             let! result = computation
//             return Ok (result, state, events)
//         })
//
//     // Lift a port (IO operation)
//     let usePort (port: Port<'a, 'b>) (input: 'a) : Workflow<'State, 'Event, 'b> =
//         Workflow (fun (state, events) -> async {
//             match! port input with
//             | Ok result -> return Ok (result, state, events)
//             | Error e -> return Error e
//         })
//
//     // Map over the result
//     let map (f: 'a -> 'b) (Workflow w) : Workflow<'State, 'Event, 'b> =
//         Workflow (fun (state, events) -> async {
//             match! w (state, events) with
//             | Ok (a, state', events') -> return Ok (f a, state', events')
//             | Error e -> return Error e
//         })
//
//     // Apply a function in the workflow context
//     let apply (Workflow f) (Workflow x) : Workflow<'State, 'Event, 'b> =
//         Workflow (fun (state, events) -> async {
//             match! f (state, events) with
//             | Ok (func, state', events') ->
//                 match! x (state', events') with
//                 | Ok (value, state'', events'') ->
//                     return Ok (func value, state'', events'')
//                 | Error e -> return Error e
//             | Error e -> return Error e
//         })
//
//     // Fold over a list within the workflow
//     let fold (f: 'a -> 'b -> Workflow<'State, 'Event, 'a>)
//              (initial: 'a)
//              (items: 'b list) : Workflow<'State, 'Event, 'a> =
//         items |> List.fold (fun acc item ->
//             workflow {
//                 let! currentAcc = acc
//                 return! f currentAcc item
//             }
//         ) (workflow.Return initial)
//
//     // Conditional execution
//     let ifThenElse (condition: 'State -> bool)
//                    (trueBranch: Workflow<'State, 'Event, 'a>)
//                    (falseBranch: Workflow<'State, 'Event, 'a>) : Workflow<'State, 'Event, 'a> =
//         Workflow (fun (state, events) -> async {
//             if condition state then
//                 let (Workflow f) = trueBranch
//                 return! f (state, events)
//             else
//                 let (Workflow f) = falseBranch
//                 return! f (state, events)
//         })
//
//     // Error handling
//     let catch (Workflow f) (handler: string -> Workflow<'State, 'Event, 'a>) : Workflow<'State, 'Event, 'a> =
//         Workflow (fun (state, events) -> async {
//             match! f (state, events) with
//             | Ok result -> return Ok result
//             | Error e ->
//                 let (Workflow h) = handler e
//                 return! h (state, events)
//         })
//
// // Example usage
// module Example =
//     // Domain types
//     type AccountId = AccountId of Guid
//
//     type AccountState = {
//         Id: AccountId
//         Balance: decimal
//         IsActive: bool
//         Version: int
//     }
//
//     type AccountCommand =
//         | OpenAccount of id: AccountId * initialBalance: decimal
//         | Deposit of amount: decimal
//         | Withdraw of amount: decimal
//         | CloseAccount
//
//     type AccountEvent =
//         | AccountOpened of AccountId * decimal
//         | Deposited of decimal
//         | Withdrawn of decimal
//         | AccountClosed
//
//     // Command handler
//     let handleAccountCommand (state: AccountState) (cmd: AccountCommand) : Result<AccountState * AccountEvent list, string> =
//         match cmd with
//         | OpenAccount (id, initialBalance) when state.Id = AccountId Guid.Empty ->
//             Ok ({ state with Id = id; Balance = initialBalance; IsActive = true },
//                 [AccountOpened (id, initialBalance)])
//
//         | Deposit amount when state.IsActive && amount > 0m ->
//             Ok ({ state with Balance = state.Balance + amount },
//                 [Deposited amount])
//
//         | Withdraw amount when state.IsActive && amount > 0m && state.Balance >= amount ->
//             Ok ({ state with Balance = state.Balance - amount },
//                 [Withdrawn amount])
//
//         | CloseAccount when state.IsActive ->
//             Ok ({ state with IsActive = false },
//                 [AccountClosed])
//
//         | _ -> Error "Invalid command for current state"
//
//     // Example ports
//     let notificationPort: Port<string, unit> =
//         fun message -> async {
//             printfn "Notification: %s" message
//             return Ok ()
//         }
//
//     let auditLogPort: Port<AccountEvent list, unit> =
//         fun events -> async {
//             printfn "Audit log: %A" events
//             return Ok ()
//         }
//
//     // Complex workflow example
//     let transferWorkflow (fromId: AccountId) (toId: AccountId) (amount: decimal) =
//         workflow {
//             // Withdraw from source account
//             do! Workflow.execute handleAccountCommand (Withdraw amount)
//
//             // Get current state after withdrawal
//             let! currentState = Workflow.getState
//
//             // Send notification
//             do! Workflow.usePort notificationPort $"Withdrawn {amount} from account"
//
//             // This would normally involve loading the target account
//             // For demo purposes, we'll just emit events
//             do! Workflow.emit (Deposited amount)
//
//             // Audit the transaction
//             let! state = Workflow.getState
//             do! Workflow.usePort auditLogPort [Withdrawn amount; Deposited amount]
//
//             return $"Transfer of {amount} completed successfully"
//         }
//
//     // Running the workflow
//     let example() = async {
//         let initialState = {
//             Id = AccountId (Guid.NewGuid())
//             Balance = 1000m
//             IsActive = true
//             Version = 1
//         }
//
//         match! Workflow.run initialState (transferWorkflow (initialState.Id) (AccountId (Guid.NewGuid())) 100m) with
//         | Ok (message, result) ->
//             printfn "Success: %s" message
//             printfn "Final state: %A" result.State
//             printfn "Events: %A" result.Events
//         | Error e ->
//             printfn "Error: %s" e
//     }

//2
// module Domain.Workflow
//
// open System
//
// // Core domain types
// type AggregateId = AggregateId of Guid
// type EventMetadata = {
//     AggregateId: AggregateId
//     Timestamp: DateTimeOffset
//     Version: int
//     CorrelationId: Guid
// }
//
// // Base types for commands and events
// type ICommand = interface end
// type IDomainEvent = interface end
//
// // ============================================================================
// // AGGREGATE OPERATIONS - Pure domain logic for single aggregate manipulation
// // ============================================================================
//
// // Result of executing a command on an aggregate
// type CommandResult<'State, 'Event> = {
//     State: 'State
//     Events: 'Event list
// }
//
// // Aggregate command handler type
// type CommandHandler<'State, 'Command, 'Event> = 'State -> 'Command -> Result<CommandResult<'State, 'Event>, string>
//
// // Aggregate behavior definition
// type AggregateBehavior<'State, 'Command, 'Event> = {
//     Initial: 'State
//     Execute: CommandHandler<'State, 'Command, 'Event>
//     Apply: 'State -> 'Event -> 'State
// }
//
// // Pure aggregate operations
// module Aggregate =
//     // Execute a single command on an aggregate
//     let execute (behavior: AggregateBehavior<'State, 'Command, 'Event>)
//                 (state: 'State)
//                 (command: 'Command) : Result<CommandResult<'State, 'Event>, string> =
//         behavior.Execute state command
//
//     // Apply events to rebuild state
//     let apply (behavior: AggregateBehavior<'State, 'Command, 'Event>)
//               (state: 'State)
//               (events: 'Event list) : 'State =
//         events |> List.fold behavior.Apply state
//
//     // Load aggregate from events
//     let loadFromEvents (behavior: AggregateBehavior<'State, 'Command, 'Event>)
//                        (events: 'Event list) : 'State =
//         apply behavior behavior.Initial events
//
// // ============================================================================
// // WORKFLOW - Business use cases with IO and orchestration
// // ============================================================================
//
// // Port abstraction for IO operations
// type Port<'a, 'b> = 'a -> Async<Result<'b, string>>
//
// // Workflow context containing multiple aggregates and external dependencies
// type WorkflowContext = {
//     CorrelationId: Guid
//     Timestamp: DateTimeOffset
// }
//
// // The Workflow monad - represents a business workflow with IO
// type Workflow<'a> =
//     | Workflow of (WorkflowContext -> Async<Result<'a, string>>)
//
// // Workflow computation expression builder
// type WorkflowBuilder() =
//     member _.Return(x) : Workflow<'a> =
//         Workflow (fun _ -> async { return Ok x })
//
//     member _.ReturnFrom(workflow: Workflow<'a>) : Workflow<'a> =
//         workflow
//
//     member _.Zero() : Workflow<unit> =
//         Workflow (fun _ -> async { return Ok () })
//
//     member _.Bind(Workflow f, g: 'a -> Workflow<'b>) : Workflow<'b> =
//         Workflow (fun ctx -> async {
//             match! f ctx with
//             | Ok a ->
//                 let (Workflow h) = g a
//                 return! h ctx
//             | Error e ->
//                 return Error e
//         })
//
//     member _.Combine(Workflow f, Workflow g) : Workflow<'b> =
//         Workflow (fun ctx -> async {
//             match! f ctx with
//             | Ok _ -> return! g ctx
//             | Error e -> return Error e
//         })
//
//     member _.Delay(f: unit -> Workflow<'a>) : Workflow<'a> =
//         f()
//
//     member _.TryWith(Workflow f, handler: exn -> Workflow<'a>) : Workflow<'a> =
//         Workflow (fun ctx -> async {
//             try
//                 return! f ctx
//             with
//             | ex ->
//                 let (Workflow h) = handler ex
//                 return! h ctx
//         })
//
//     member _.TryFinally(Workflow f, compensation: unit -> unit) : Workflow<'a> =
//         Workflow (fun ctx -> async {
//             try
//                 return! f ctx
//             finally
//                 compensation()
//         })
//
// // Create the workflow computation expression
// let workflow = WorkflowBuilder()
//
// // Core workflow operations
// module Workflow =
//     // Run a workflow
//     let run (context: WorkflowContext) (Workflow f) : Async<Result<'a, string>> =
//         f context
//
//     // Get workflow context
//     let getContext : Workflow<WorkflowContext> =
//         Workflow (fun ctx -> async { return Ok ctx })
//
//     // Lift a pure value
//     let pure' (value: 'a) : Workflow<'a> =
//         Workflow (fun _ -> async { return Ok value })
//
//     // Lift an async computation
//     let liftAsync (computation: Async<'a>) : Workflow<'a> =
//         Workflow (fun _ -> async {
//             let! result = computation
//             return Ok result
//         })
//
//     // Use a port (IO operation)
//     let usePort (port: Port<'a, 'b>) (input: 'a) : Workflow<'b> =
//         Workflow (fun _ -> port input)
//
//     // Handle errors
//     let catch (Workflow f) (handler: string -> Workflow<'a>) : Workflow<'a> =
//         Workflow (fun ctx -> async {
//             match! f ctx with
//             | Ok result -> return Ok result
//             | Error e ->
//                 let (Workflow h) = handler e
//                 return! h ctx
//         })
//
//     // Map over the result
//     let map (f: 'a -> 'b) (Workflow w) : Workflow<'b> =
//         Workflow (fun ctx -> async {
//             match! w ctx with
//             | Ok a -> return Ok (f a)
//             | Error e -> return Error e
//         })
//
//     // Parallel execution
//     let parallel (workflows: Workflow<'a> list) : Workflow<'a list> =
//         Workflow (fun ctx -> async {
//             let! results =
//                 workflows
//                 |> List.map (fun (Workflow f) -> f ctx)
//                 |> Async.Parallel
//
//             let errors = results |> Array.choose (function Error e -> Some e | _ -> None)
//             if Array.isEmpty errors then
//                 let values = results |> Array.choose (function Ok v -> Some v | _ -> None) |> Array.toList
//                 return Ok values
//             else
//                 return Error (String.concat "; " errors)
//         })
//
// // ============================================================================
// // AGGREGATE SERVICE - Bridge between pure aggregates and workflows
// // ============================================================================
//
// // Aggregate service that handles loading, executing commands, and saving
// type AggregateService<'State, 'Command, 'Event> = {
//     Load: AggregateId -> Async<Result<('State * 'Event list), string>>
//     Save: AggregateId -> 'Event list -> Async<Result<unit, string>>
// }
//
// // Operations for working with aggregates within workflows
// module AggregateWorkflow =
//     // Execute a command on an aggregate within a workflow
//     let executeCommand (service: AggregateService<'State, 'Command, 'Event>)
//                        (behavior: AggregateBehavior<'State, 'Command, 'Event>)
//                        (aggregateId: AggregateId)
//                        (command: 'Command) : Workflow<CommandResult<'State, 'Event>> =
//         workflow {
//             // Load aggregate
//             let! loadResult = Workflow.liftAsync (service.Load aggregateId)
//             let! (currentState, existingEvents) =
//                 match loadResult with
//                 | Ok result -> Workflow.pure' result
//                 | Error e -> Workflow.pure' (behavior.Initial, [])
//
//             // Execute command
//             let commandResult = Aggregate.execute behavior currentState command
//             match commandResult with
//             | Ok result ->
//                 // Save new events
//                 do! Workflow.liftAsync (service.Save aggregateId result.Events)
//                 return result
//             | Error e ->
//                 return! Workflow.catch (Workflow (fun _ -> async { return Error e })) (fun _ -> Workflow.pure' { State = currentState; Events = [] })
//         }
//
//     // Load an aggregate within a workflow
//     let load (service: AggregateService<'State, 'Command, 'Event>)
//              (behavior: AggregateBehavior<'State, 'Command, 'Event>)
//              (aggregateId: AggregateId) : Workflow<'State> =
//         workflow {
//             let! loadResult = Workflow.liftAsync (service.Load aggregateId)
//             match loadResult with
//             | Ok (state, _) -> return state
//             | Error _ -> return behavior.Initial
//         }
//
// // ============================================================================
// // EXAMPLE USAGE
// // ============================================================================
//
// module Example =
//     // Domain types
//     type AccountId = AccountId of Guid
//
//     type AccountState = {
//         Id: AccountId
//         Balance: decimal
//         IsActive: bool
//         Version: int
//     }
//
//     type AccountCommand =
//         | OpenAccount of id: AccountId * initialBalance: decimal
//         | Deposit of amount: decimal
//         | Withdraw of amount: decimal
//         | CloseAccount
//
//     type AccountEvent =
//         | AccountOpened of AccountId * decimal
//         | Deposited of decimal
//         | Withdrawn of decimal
//         | AccountClosed
//
//     // Pure aggregate behavior
//     let accountBehavior : AggregateBehavior<AccountState, AccountCommand, AccountEvent> =
//         let execute state cmd =
//             match cmd with
//             | OpenAccount (id, initialBalance) when state.Id = AccountId Guid.Empty ->
//                 Ok {
//                     State = { state with Id = id; Balance = initialBalance; IsActive = true }
//                     Events = [AccountOpened (id, initialBalance)]
//                 }
//
//             | Deposit amount when state.IsActive && amount > 0m ->
//                 Ok {
//                     State = { state with Balance = state.Balance + amount }
//                     Events = [Deposited amount]
//                 }
//
//             | Withdraw amount when state.IsActive && amount > 0m && state.Balance >= amount ->
//                 Ok {
//                     State = { state with Balance = state.Balance - amount }
//                     Events = [Withdrawn amount]
//                 }
//
//             | CloseAccount when state.IsActive ->
//                 Ok {
//                     State = { state with IsActive = false }
//                     Events = [AccountClosed]
//                 }
//
//             | _ -> Error "Invalid command for current state"
//
//         let apply state event =
//             match event with
//             | AccountOpened (id, balance) -> { state with Id = id; Balance = balance; IsActive = true }
//             | Deposited amount -> { state with Balance = state.Balance + amount }
//             | Withdrawn amount -> { state with Balance = state.Balance - amount }
//             | AccountClosed -> { state with IsActive = false }
//
//         {
//             Initial = { Id = AccountId Guid.Empty; Balance = 0m; IsActive = false; Version = 0 }
//             Execute = execute
//             Apply = apply
//         }
//
//     // IO Ports
//     let notificationPort: Port<string, unit> =
//         fun message -> async {
//             printfn "Notification: %s" message
//             return Ok ()
//         }
//
//     let emailPort: Port<(string * string), unit> =
//         fun (recipient, message) -> async {
//             printfn "Email to %s: %s" recipient message
//             return Ok ()
//         }
//
//     let fraudCheckPort: Port<decimal, bool> =
//         fun amount -> async {
//             // Simulate fraud check
//             return Ok (amount < 10000m)
//         }
//
//     // Business Workflow: Money Transfer
//     let transferWorkflow (accountService: AggregateService<AccountState, AccountCommand, AccountEvent>)
//                          (fromId: AccountId)
//                          (toId: AccountId)
//                          (amount: decimal) : Workflow<string> =
//         workflow {
//             let! context = Workflow.getContext
//
//             // 1. Fraud check
//             let! fraudCheckPassed = Workflow.usePort fraudCheckPort amount
//             if not fraudCheckPassed then
//                 do! Workflow.usePort notificationPort "Fraud alert: Large transfer blocked"
//                 return "Transfer blocked by fraud check"
//             else
//                 // 2. Execute withdrawal
//                 let! withdrawResult =
//                     AggregateWorkflow.executeCommand accountService accountBehavior fromId (Withdraw amount)
//
//                 if withdrawResult.Events.IsEmpty then
//                     return "Withdrawal failed"
//                 else
//                     // 3. Execute deposit
//                     let! depositResult =
//                         AggregateWorkflow.executeCommand accountService accountBehavior toId (Deposit amount)
//
//                     // 4. Send notifications
//                     do! Workflow.usePort notificationPort $"Transfer of {amount} completed"
//                     do! Workflow.usePort emailPort ("customer@example.com", $"You transferred {amount}")
//
//                     return $"Transfer completed: {amount} from {fromId} to {toId}"
//         }
//
//     // Business Workflow: Account Opening
//     let openAccountWorkflow (accountService: AggregateService<AccountState, AccountCommand, AccountEvent>)
//                             (customerId: string)
//                             (initialDeposit: decimal) : Workflow<AccountId> =
//         workflow {
//             let accountId = AccountId (Guid.NewGuid())
//
//             // 1. Create account
//             let! result =
//                 AggregateWorkflow.executeCommand accountService accountBehavior
//                     accountId (OpenAccount (accountId, initialDeposit))
//
//             // 2. Send welcome email
//             do! Workflow.usePort emailPort (customerId, "Welcome to our bank!")
//
//             // 3. Register for fraud monitoring
//             do! Workflow.usePort notificationPort $"New account {accountId} registered for monitoring"
//
//             return accountId
//         }
//
//     // Mock aggregate service
//     let createInMemoryService() =
//         let mutable store = Map.empty<AggregateId, AccountEvent list>
//
//         {
//             Load = fun id -> async {
//                 match Map.tryFind id store with
//                 | Some events ->
//                     let state = Aggregate.loadFromEvents accountBehavior events
//                     return Ok (state, events)
//                 | None ->
//                     return Ok (accountBehavior.Initial, [])
//             }
//
//             Save = fun id events -> async {
//                 store <-
//                     match Map.tryFind id store with
//                     | Some existing -> Map.add id (existing @ events) store
//                     | None -> Map.add id events store
//                 return Ok ()
//             }
//         }
//
//     // Running the example
//     let example() = async {
//         let service = createInMemoryService()
//         let context = { CorrelationId = Guid.NewGuid(); Timestamp = DateTimeOffset.UtcNow }
//
//         // Open two accounts
//         let! account1Result = Workflow.run context (openAccountWorkflow service "john@example.com" 1000m)
//         let! account2Result = Workflow.run context (openAccountWorkflow service "jane@example.com" 500m)
//
//         match account1Result, account2Result with
//         | Ok account1, Ok account2 ->
//             // Transfer money
//             let! transferResult = Workflow.run context (transferWorkflow service account1 account2 100m)
//             match transferResult with
//             | Ok message -> printfn "Success: %s" message
//             | Error e -> printfn "Transfer failed: %s" e
//         | _ ->
//             printfn "Failed to create accounts"
//     }
