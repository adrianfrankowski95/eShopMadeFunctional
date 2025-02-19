namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data
open System.Text.Json
open System.Threading
open DbUp
open FsToolkit.ErrorHandling
open Dapper
open eShop.DomainDrivenDesign

[<RequireQualifiedAccess>]
module EventHandling =

    [<RequireQualifiedAccess>]
    module Db =
        let init = Db.init "./dbinit/"

    type EventId = private EventId of Guid

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

    let private (|SqlConnection|) =
        function
        | WithTransaction dbTransaction -> (dbTransaction.Connection, Some dbTransaction)
        | WithoutTransaction dbConnection -> (dbConnection, None)

    let persistEvents
        (jsonOptions: JsonSerializerOptions)
        (dbTransaction: IDbTransaction)
        : PersistEvents<'state, EventId, 'eventPayload, SqlIoError list> =
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
        (SqlConnection(dbConnection, transactionOption))
        : ReadUnprocessedEvents<EventId, 'eventPayload, SqlIoError list> =
        fun aggregateType ->
            dbConnection.QueryAsync<Dto.Event>(
                Sql.ReadUnprocessedEvents,
                {| AggregateType = aggregateType |},
                transactionOption |> Option.toObj
            )
            |> toAsyncResult SqlException
            |> AsyncResult.map List.ofSeq
            |> AsyncResult.mapError List.singleton
            |> AsyncResult.bind (
                List.traverseResultA (Dto.Event.toDomain<'eventPayload> jsonOptions)
                >> Result.map Map.ofSeq
                >> AsyncResult.ofResult
            )

    let persistSuccessfulEventHandlers
        (SqlConnection(dbConnection, transactionOption))
        : PersistSuccessfulEventHandlers<EventId, SqlIoError> =
        fun (EventId eventId) successfulHandlers ->
            dbConnection.ExecuteAsync(
                Sql.PersistSuccessfulEventHandlers,
                {| EventId = eventId
                   SuccessfulHandlers = successfulHandlers |> Set.toArray |},
                transactionOption |> Option.toObj
            )
            |> toAsyncResult SqlException
            |> AsyncResult.ignore

    let markEventAsProcessed
        (getNow: GetUtcNow)
        (SqlConnection(dbConnection, transactionOption))
        : MarkEventAsProcessed<EventId, SqlIoError> =
        fun (EventId eventId) ->
            dbConnection.ExecuteAsync(
                Sql.MarkEventAsProcessed,
                {| EventId = eventId
                   UtcNow = getNow () |},
                transactionOption |> Option.toObj
            )
            |> toAsyncResult SqlException
            |> AsyncResult.ignore
