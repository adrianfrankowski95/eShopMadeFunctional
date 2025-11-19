[<RequireQualifiedAccess>]
module eShop.RabbitMQ

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open eShop.RabbitMQ
open eShop.DomainDrivenDesign
open FsToolkit.ErrorHandling
open System.Threading.Channels
open eShop.Prelude

type private ReplyChannel<'t> = 't -> unit

type private Message =
    | Publish of EventName * Event<obj> * Priority * ReplyChannel<Result<unit, PublishError>>
    | Consume of EventName * Event<byte array> * Priority * ReplyChannel<Result<unit, EventHandlingError<obj>>>

module private Message =
    let getPriority =
        function
        | Publish(_, _, p, _) ->
            match p with
            | Priority.Regular -> 0
            | Priority.Low -> 1
        | Consume(_, _, p, _) ->
            match p with
            | Priority.Regular -> 2
            | Priority.Low -> 3

type Agent
    internal
    (eventBus: EventBus, eventHandlers: EventHandlers, jsonOptions: JsonSerializerOptions, logger: ILogger<RabbitMQ>) =

    let comparer =
        Comparer.Create(fun a b -> (a, b) |> Tuple.mapBoth Message.getPriority |> (fun m -> m ||> compare))

    let options =
        UnboundedPrioritizedChannelOptions<Message>(
            SingleReader = true,
            AllowSynchronousContinuations = true,
            Comparer = comparer
        )

    let cts = new CancellationTokenSource()

    let channel = Channel.CreateUnboundedPrioritized<Message>(options)

    let publishEvent = Publisher.internalPublish jsonOptions
    let handleEvent = eventHandlers |> EventHandlers.invoke jsonOptions
    let subscribe = Consumer.subscribe logger

    let rec loop (reader: ChannelReader<Message>, publisher: Publisher, consumer: Consumer) =
        backgroundTask {
            match cts.IsCancellationRequested with
            | true ->
                do! publisher |> Publisher.dispose
                do! consumer |> Consumer.dispose
                return ()
            
            | false ->
                let! msg = reader.ReadAsync(cts.Token)

                match msg with
                | Publish(evName, ev, priority, reply) ->
                    do! publisher |> publishEvent evName ev priority |> Task.map reply

                | Consume(evName, ev, _, reply) -> do! ev |> handleEvent evName |> Task.map reply

                return! loop (reader, publisher, consumer)
        }

    let _ =
        backgroundTask {
            let! publisher = eventBus |> EventBus.createPublisher
            let! consumer = eventBus |> EventBus.createConsumer

            consumer
            |> subscribe (fun evName ev priority ->
                let tcs = TaskCompletionSource<_>()

                channel.Writer.WriteAsync(Consume(evName, ev, priority, tcs.SetResult), cts.Token).AsTask()
                |> Task.ofUnit
                |> TaskResult.ofTask
                |> TaskResult.bind (fun _ -> tcs.Task))

            return! loop (channel.Reader, publisher, consumer)
        }
    
    member _.Publish<'payload> (ev: Event<'payload>) (priority: Priority) =
        let evName = ev |> Publisher.getEventName
        let boxedEv = ev |> Event.mapPayload box
        
        let tcs = TaskCompletionSource<_>()

        channel.Writer.WriteAsync(Publish(evName, boxedEv, priority, tcs.SetResult), cts.Token).AsTask()
        |> Task.ofUnit
        |> TaskResult.ofTask
        |> TaskResult.bind (fun _ -> tcs.Task)

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            cts.Dispose()
            channel.Writer.Complete()
