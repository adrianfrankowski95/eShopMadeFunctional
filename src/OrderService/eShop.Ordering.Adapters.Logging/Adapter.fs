namespace eShop.Ordering.Adapters

open eShop.DomainDrivenDesign
open Microsoft.Extensions.Logging
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Logger =
    let inline logEventsProcessorError
        (logger: ILogger<'t>)
        (error: EventsProcessor.EventsProcessorError<'state, _, 'ioError, _>)
        =
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

    let inline logEvent (logger: ILogger<'t>): EventHandler<'state, _, _> =
        fun (aggregateId: AggregateId<'state>) event ->
            logger.LogInformation(
                "An Event occurred for Aggregate {AggregateType} - {AggregateId}: {Event}",
                Aggregate.typeName<'state>,
                aggregateId |> AggregateId.value,
                event
            )
            |> TaskResult.ok