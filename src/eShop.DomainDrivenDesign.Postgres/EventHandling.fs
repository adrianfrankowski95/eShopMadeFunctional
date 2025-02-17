[<RequireQualifiedAccess>]
module eShop.DomainDrivenDesign.Postgres

open System
open System.Data
open System.Text.Json
open FsToolkit.ErrorHandling
open Dapper

[<RequireQualifiedAccess>]
module EventHandling =
    type EventId = private EventId of Guid

    [<RequireQualifiedAccess>]
    module EventId =
        let value (EventId value) = value

    module private Json =
        let serialize (jsonOptions: JsonSerializerOptions) (data: 'eventPayload) =
            JsonSerializer.Serialize<'eventPayload>(data, jsonOptions)

        let deserialize<'eventPayload> (jsonOptions: JsonSerializerOptions) (payload: string) =
            JsonSerializer.Deserialize<'eventPayload>(payload, jsonOptions)

    module private Sql =
        [<Literal>]
        let PersistEvent =
            """INSERT INTO "eShop"."EventProcessingLog" 
            ("AggregateId", "AggregateType", "EventData", "OccurredAt", "SuccessfulHandlers")
            VALUES 
            (@AggregateId, @AggregateType, @EventData, @OccurredAt, {})
            RETURNING "EventId";"""

        [<Literal>]
        let ReadUnprocessedEvents =
            """SELECT "EventId", "AggregateType", "EventData", "OccurredAt", "SuccessfulHandlers"
            FROM "eShop"."EventProcessingLog"
            WHERE "AggregateType" = @AggregateType
            AND "ProcessedAt" IS NULL
            ORDER BY "OccurredAt" ASC;"""

        [<Literal>]
        let PersistSuccessfulEventHandlers =
            """UPDATE "eShop"."EventProcessingLog"
            SET "SuccessfulHandlers" = "SuccessfulHandlers" || @SuccessfulHandlers
            WHERE "EventId" = @EventId;"""

        [<Literal>]
        let MarkEventAsProcessed =
            """UPDATE "eShop"."EventProcessingLog"
            SET "ProcessedAt" = @UtcNow
            WHERE "EventId" = @EventId;"""

    module private Dto =
        [<CLIMutable>]
        type Event =
            { EventId: Guid
              EventData: string
              OccurredAt: DateTimeOffset
              SuccessfulHandlers: string array }

        module Event =
            let toDomain<'eventPayload> jsonOptions dto =
                (dto.EventId |> EventId,
                 { Data = dto.EventData |> Json.deserialize<'eventPayload> jsonOptions
                   OccurredAt = dto.OccurredAt }),
                dto.SuccessfulHandlers |> Set.ofArray

    let private toAsyncResult x =
        try
            x |> AsyncResult.ofTask
        with e ->
            e |> AsyncResult.error

    let persistEvents
        (jsonOptions: JsonSerializerOptions)
        (dbConnection: IDbConnection)
        : PersistEvents<'state, EventId, 'eventPayload, exn> =
        fun (AggregateId aggregateId) events ->
            events
            |> List.traverseAsyncResultA (fun ev ->
                let param =
                    {| AggregateId = aggregateId
                       AggregateType = typeof<'state>.Name
                       EventData = ev.Data |> Json.serialize jsonOptions
                       OccurredAt = ev.OccurredAt |}

                dbConnection.ExecuteScalarAsync<Guid>(Sql.PersistEvent, param)
                |> toAsyncResult
                |> AsyncResult.map (fun evId -> evId |> EventId, ev))
            |> AsyncResult.mapError (fun ex -> ex |> AggregateException :> exn)

    let readUnprocessedEvents
        (jsonOptions: JsonSerializerOptions)
        (dbConnection: IDbConnection)
        : ReadUnprocessedEvents<EventId, 'eventPayload, exn> =
        fun aggregateType ->
            dbConnection.QueryAsync<Dto.Event>(Sql.ReadUnprocessedEvents, {| AggregateType = aggregateType |})
            |> toAsyncResult
            |> AsyncResult.map (Seq.map (Dto.Event.toDomain<'eventPayload> jsonOptions) >> Map.ofSeq)

    let persistSuccessfulEventHandlers (dbConnection: IDbConnection) : PersistSuccessfulEventHandlers<EventId, exn> =
        fun (EventId eventId) successfulHandlers ->
            dbConnection.ExecuteAsync(
                Sql.PersistSuccessfulEventHandlers,
                {| EventId = eventId
                   SuccessfulHandlers = successfulHandlers |> Set.toArray |}
            )
            |> toAsyncResult
            |> AsyncResult.ignore

    let markEventAsProcessed (getNow: GetUtcNow) (dbConnection: IDbConnection) : MarkEventAsProcessed<EventId, exn> =
        fun (EventId eventId) ->
            dbConnection.ExecuteAsync(
                Sql.MarkEventAsProcessed,
                {| EventId = eventId
                   UtcNow = getNow () |}
            )
            |> toAsyncResult
            |> AsyncResult.ignore
