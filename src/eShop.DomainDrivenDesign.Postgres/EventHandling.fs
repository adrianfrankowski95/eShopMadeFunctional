namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data.Common
open System.Text.Json
open FsToolkit.ErrorHandling
open eShop.DomainDrivenDesign
open eShop.Postgres

[<RequireQualifiedAccess>]
module Postgres =
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
        let inline persistEvent (DbSchema schema) =
            $"""INSERT INTO "%s{schema}"."EventProcessingLog" 
            ("AggregateId", "AggregateType", "EventData", "OccurredAt")
            VALUES 
            (@AggregateId, @AggregateType, @EventData, @OccurredAt)
            RETURNING "EventId";"""

        let inline readUnprocessedEvents (DbSchema schema) =
            $"""SELECT "EventId", "AggregateType", "EventData", "OccurredAt", "SuccessfulHandlers"
            FROM "%s{schema}"."EventProcessingLog"
            WHERE "AggregateType" = @AggregateType
            AND "ProcessedAt" IS NULL
            ORDER BY "OccurredAt" ASC;"""

        let inline persistSuccessfulEventHandlers (DbSchema schema) =
            $"""UPDATE "%s{schema}"."EventProcessingLog"
            SET "SuccessfulHandlers" = "SuccessfulHandlers" || @SuccessfulHandlers
            WHERE "EventId" = @EventId;"""

        let inline markEventAsProcessed (DbSchema schema) =
            $"""UPDATE "%s{schema}"."EventProcessingLog"
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
            let toDomain<'dto, 'eventData> (toDomain: 'dto -> Result<'eventData, string>) jsonOptions dto =
                dto.EventData
                |> Json.deserialize<'dto> jsonOptions
                |> Result.bind (toDomain >> Result.mapError InvalidData)
                |> Result.map (fun evData ->
                    (dto.EventId |> EventId,
                     { Data = evData
                       OccurredAt = dto.OccurredAt }),
                    dto.SuccessfulHandlers |> Set.ofArray)

    let persistEvents
        (eventPayloadToDto: 'eventPayload -> 'dto)
        (jsonOptions: JsonSerializerOptions)
        (dbSchema: DbSchema)
        (dbTransaction: DbTransaction)
        : PersistEvents<'state, EventId, 'eventPayload, SqlIoError list> =
        fun (AggregateId aggregateId) events ->
            events
            |> List.traverseAsyncResultA (fun ev ->
                asyncResult {
                    let! eventData = ev.Data |> eventPayloadToDto |> Json.serialize jsonOptions

                    let param =
                        {| AggregateId = aggregateId
                           AggregateType = typeof<'state>.Name
                           EventData = eventData
                           OccurredAt = ev.OccurredAt |}

                    return!
                        dbTransaction
                        |> SqlSession.Sustained
                        |> Dapper.executeScalar<Guid> (Sql.persistEvent dbSchema) param
                        |> AsyncResult.map (fun evId -> evId |> EventId, ev)
                })

    let readUnprocessedEvents
        (dtoToEventPayload: 'dto -> Result<'eventPayload, string>)
        (jsonOptions: JsonSerializerOptions)
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : ReadUnprocessedEvents<EventId, 'eventPayload, SqlIoError list> =
        fun aggregateType ->
            let param = {| AggregateType = aggregateType |}

            sqlSession
            |> Dapper.query<Dto.Event> (Sql.readUnprocessedEvents dbSchema) param
            |> AsyncResult.map List.ofSeq
            |> AsyncResult.mapError List.singleton
            |> AsyncResult.bind (
                List.traverseResultA (Dto.Event.toDomain<'dto, 'eventPayload> dtoToEventPayload jsonOptions)
                >> Result.map Map.ofSeq
                >> AsyncResult.ofResult
            )

    let persistSuccessfulEventHandlers
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : PersistSuccessfulEventHandlers<EventId, SqlIoError> =
        fun (EventId eventId) successfulHandlers ->
            let param =
                {| EventId = eventId
                   SuccessfulHandlers = successfulHandlers |> Set.toArray |}

            sqlSession
            |> Dapper.execute (Sql.persistSuccessfulEventHandlers dbSchema) param
            |> AsyncResult.ignore

    let markEventAsProcessed
        (getNow: GetUtcNow)
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : MarkEventAsProcessed<EventId, SqlIoError> =
        fun (EventId eventId) ->
            let param =
                {| EventId = eventId
                   UtcNow = getNow () |}

            sqlSession
            |> Dapper.execute (Sql.markEventAsProcessed dbSchema) param
            |> AsyncResult.ignore
