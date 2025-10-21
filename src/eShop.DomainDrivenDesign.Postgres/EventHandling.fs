namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data.Common
open System.Text.Json
open FsToolkit.ErrorHandling
open eShop.DomainDrivenDesign
open eShop.Postgres

[<RequireQualifiedAccess>]
module Postgres =
    module private Json =
        let serialize (jsonOptions: JsonSerializerOptions) (data: 'eventPayload) =
            try
                JsonSerializer.Serialize<'eventPayload>(data, jsonOptions) |> Ok
            with e ->
                e |> SerializationError |> Error

        let deserialize<'eventPayload> (jsonOptions: JsonSerializerOptions) (payload: string) =
            try
                JsonSerializer.Deserialize<'eventPayload>(payload, jsonOptions) |> Ok
            with e ->
                e |> DeserializationError |> Error

    module private Sql =
        module DomainEventLog =
            let inline persistEvent (DbSchema schema) =
                $"""INSERT INTO "%s{schema}".domain_event_log 
                (event_id, aggregate_id, aggregate_type, event_type, event_data, occurred_at)
                VALUES (@EventId, @AggregateId, @AggregateType, @EventType, @EventData::jsonb, @OccurredAt)"""

            let inline readUnprocessedEvents (DbSchema schema) =
                $"""SELECT event_id as "EventId", aggregate_id as "AggregateId", aggregate_type as "AggregateType",
                event_data as "EventData", event_type as "EventType", occurred_at as "OccurredAt", successful_handlers as "SuccessfulHandlers"
                FROM "%s{schema}".domain_event_log
                WHERE aggregate_type = @AggregateType AND event_type = @EventType AND processed_at IS NULL
                ORDER BY occurred_at ASC"""

            let inline persistSuccessfulEventHandlers (DbSchema schema) =
                $"""UPDATE "%s{schema}".domain_event_log
                SET successful_handlers = successful_handlers || @SuccessfulHandlers
                WHERE event_id = @EventId"""

            let inline markEventAsProcessed (DbSchema schema) =
                $"""UPDATE "%s{schema}".domain_event_log
                SET processed_at = @UtcNow
                WHERE event_id = @EventId"""
                
        module InboxEventLog =
            let inline persistEvent (DbSchema schema) =
                $"""INSERT INTO "%s{schema}".inbox_event_log 
                (event_id, event_type, event_data, occurred_at)
                VALUES (@EventId, @EventType, @EventData::jsonb, @OccurredAt)"""

            // FOR UPDATE SKIP LOCKED prevents multiple instances from processing the same events
            let inline readUnprocessedEvents (DbSchema schema) =
                $"""SELECT event_id as "EventId", event_data as "EventData",
                event_type as "EventType", occurred_at as "OccurredAt"
                FROM "%s{schema}".inbox_event_log
                WHERE processed_at IS NULL
                FOR UPDATE SKIP LOCKED
                ORDER BY occurred_at ASC
                LIMIT 100"""

            let inline markEventAsProcessed (DbSchema schema) =
                $"""UPDATE "%s{schema}".inbox_event_log
                SET processed_at = @UtcNow
                WHERE event_id = @EventId"""

    module private Dto =
        [<CLIMutable>]
        type Event =
            { EventId: Guid
              AggregateId: Guid
              EventData: string
              OccurredAt: DateTimeOffset
              SuccessfulHandlers: string array }

        module Event =
            let toDomain<'state, 'eventPayloadDto, 'eventPayload>
                (parsePayload: 'eventPayloadDto -> Result<'eventPayload, string>)
                (jsonOptions: JsonSerializerOptions)
                dto
                =
                dto.EventData
                |> Json.deserialize<'eventPayloadDto> jsonOptions
                |> Result.bind (parsePayload >> Result.mapError InvalidData)
                |> Result.map (fun payload ->
                    dto.AggregateId |> AggregateId.ofGuid<'state>,
                    { Id = dto.EventId |> EventId.ofGuid
                      Data = payload
                      OccurredAt = dto.OccurredAt },
                    dto.SuccessfulHandlers |> Set.ofArray)

    type PersistEvents<'state, 'eventPayload> = PersistEvents<'state, 'eventPayload, SqlIoError>

    let persistEvents
        (payloadToDto: 'eventPayload -> 'eventPayloadDto)
        (jsonOptions: JsonSerializerOptions)
        (dbSchema: DbSchema)
        (dbConnection: DbConnection)
        : PersistEvents<'state, 'eventPayload> =
        fun (AggregateId aggregateId) events ->
            taskResult {
                let! parameters =
                    events
                    |> List.traverseResultM (fun ev ->
                        ev.Data
                        |> payloadToDto
                        |> Json.serialize jsonOptions
                        |> Result.map (fun rawData ->
                            {| EventId = ev.Id |> EventId.value
                               AggregateId = aggregateId
                               AggregateType = Aggregate.typeName<'state>
                               EventType = typeof<'eventPayload>.FullName
                               EventData = rawData
                               OccurredAt = ev.OccurredAt |}))

                do!
                    parameters
                    |> Dapper.execute dbConnection (Sql.DomainEventLog.persistEvent dbSchema)
                    |> TaskResult.ignore
            }


    type ReadUnprocessedEvents<'state, 'eventPayload> = ReadUnprocessedEvents<'state, 'eventPayload, SqlIoError>

    let readUnprocessedEvents
        (parsePayload: 'eventPayloadDto -> Result<'eventPayload, string>)
        (jsonOptions: JsonSerializerOptions)
        (dbSchema: DbSchema)
        (dbConnection: DbConnection)
        : ReadUnprocessedEvents<'state, 'eventPayload> =
        fun () ->
            {| AggregateType = Aggregate.typeName<'state>
               EventType = typeof<'eventPayload>.FullName |}
            |> Dapper.query<Dto.Event> dbConnection (Sql.DomainEventLog.readUnprocessedEvents dbSchema)
            |> TaskResult.bind (
                Seq.traverseResultM (
                    Dto.Event.toDomain<'state, 'eventPayloadDto, 'eventPayload> parsePayload jsonOptions
                )
                >> Result.map Seq.toList
                >> TaskResult.ofResult
            )

    type PersistSuccessfulEventHandlers = PersistSuccessfulEventHandlers<SqlIoError>

    let persistSuccessfulEventHandlers (dbSchema: DbSchema) (dbConnection: DbConnection) : PersistSuccessfulEventHandlers =
        fun eventId successfulHandlers ->
            {| EventId = eventId |> EventId.value
               SuccessfulHandlers = successfulHandlers |> Set.toArray |}
            |> Dapper.execute dbConnection (Sql.DomainEventLog.persistSuccessfulEventHandlers dbSchema)
            |> TaskResult.ignore

    type MarkEventAsProcessed = MarkEventAsProcessed<SqlIoError>

    let markEventAsProcessed (dbSchema: DbSchema) (dbConnection: DbConnection) (getNow: GetUtcNow) : MarkEventAsProcessed =
        fun eventId ->
            {| EventId = eventId |> EventId.value
               UtcNow = getNow () |}
            |> Dapper.execute dbConnection (Sql.DomainEventLog.markEventAsProcessed dbSchema)
            |> TaskResult.ignore
