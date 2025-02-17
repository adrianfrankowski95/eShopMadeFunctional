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

    type IoError =
        | SerializationException of exn
        | DeserializationException of exn
        | SqlException of exn

    module private Json =
        let serialize (jsonOptions: JsonSerializerOptions) (data: 'eventPayload) =
            try
                JsonSerializer.Serialize<'eventPayload>(data, jsonOptions) |> Ok
            with e ->
                e |> SerializationException |> Error

        let deserialize<'eventPayload> (jsonOptions: JsonSerializerOptions) (payload: string) =
            try
                JsonSerializer.Deserialize<'eventPayload>(payload, jsonOptions) |> Ok
            with e ->
                e |> DeserializationException |> Error

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
                dto.EventData
                |> Json.deserialize<'eventPayload> jsonOptions
                |> Result.map (fun evData ->
                    (dto.EventId |> EventId,
                     { Data = evData
                       OccurredAt = dto.OccurredAt }),
                    dto.SuccessfulHandlers |> Set.ofArray)

    let inline private toAsyncResult errMapper x =
        try
            x |> Async.AwaitTask |> Async.map Ok
        with e ->
            e |> errMapper |> AsyncResult.error

    let persistEvents
        (jsonOptions: JsonSerializerOptions)
        (dbTransaction: IDbTransaction)
        : PersistEvents<'state, EventId, 'eventPayload, IoError list> =
        fun (AggregateId aggregateId) events ->
            events
            |> List.traverseAsyncResultA (fun ev ->
                let param =
                    {| AggregateId = aggregateId
                       AggregateType = typeof<'state>.Name
                       EventData = ev.Data |> Json.serialize jsonOptions
                       OccurredAt = ev.OccurredAt |}

                dbTransaction.Connection.ExecuteScalarAsync<Guid>(Sql.PersistEvent, param, dbTransaction)
                |> toAsyncResult SqlException
                |> AsyncResult.map (fun evId -> evId |> EventId, ev))

    let readUnprocessedEvents
        (jsonOptions: JsonSerializerOptions)
        (dbConnection: IDbConnection)
        : ReadUnprocessedEvents<EventId, 'eventPayload, IoError list> =
        fun aggregateType ->
            dbConnection.QueryAsync<Dto.Event>(Sql.ReadUnprocessedEvents, {| AggregateType = aggregateType |})
            |> toAsyncResult SqlException
            |> AsyncResult.map List.ofSeq
            |> AsyncResult.mapError List.singleton
            |> AsyncResult.bind (
                List.traverseResultA (Dto.Event.toDomain<'eventPayload> jsonOptions)
                >> Result.map Map.ofSeq
                >> AsyncResult.ofResult
            )

    let persistSuccessfulEventHandlers
        (dbConnection: IDbConnection)
        : PersistSuccessfulEventHandlers<EventId, IoError> =
        fun (EventId eventId) successfulHandlers ->
            dbConnection.ExecuteAsync(
                Sql.PersistSuccessfulEventHandlers,
                {| EventId = eventId
                   SuccessfulHandlers = successfulHandlers |> Set.toArray |}
            )
            |> toAsyncResult SqlException
            |> AsyncResult.ignore

    let markEventAsProcessed
        (getNow: GetUtcNow)
        (dbConnection: IDbConnection)
        : MarkEventAsProcessed<EventId, IoError> =
        fun (EventId eventId) ->
            dbConnection.ExecuteAsync(
                Sql.MarkEventAsProcessed,
                {| EventId = eventId
                   UtcNow = getNow () |}
            )
            |> toAsyncResult SqlException
            |> AsyncResult.ignore
