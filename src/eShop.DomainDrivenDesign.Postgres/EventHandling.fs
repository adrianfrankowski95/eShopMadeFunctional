namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data.Common
open System.Text.Json
open FsToolkit.ErrorHandling
open eShop.DomainDrivenDesign
open eShop.Postgres
open eShop.Postgres.Dapper.Parameters

type EventId = private EventId of Guid

module EventId =
    let value (EventId rawId) = rawId

[<RequireQualifiedAccess>]
module Postgres =
    // module private Json =
    //     let serialize (jsonOptions: JsonSerializerOptions) (data: 'eventPayload) =
    //         try
    //             JsonSerializer.Serialize<'eventPayload>(data, jsonOptions) |> Ok
    //         with e ->
    //             e |> SerializationException |> Error
    //
    //     let deserialize<'eventPayload> (jsonOptions: JsonSerializerOptions) (payload: string) =
    //         try
    //             JsonSerializer.Deserialize<'eventPayload>(payload, jsonOptions) |> Ok
    //         with e ->
    //             e |> DeserializationException |> Error

    module private Sql =
        let inline persistEvent (DbSchema schema) =
            $"""
            WITH insertedId (id) as (SELECT gen_random_uuid())
            INSERT INTO "%s{schema}"."EventProcessingLog" 
            ("EventId", "CorrelationId", "AggregateId", "AggregateType", "EventType", "EventData", "OccurredAt")
            VALUES (insertedId, COALESCE(@CorrelationId, insertedId), @AggregateId, @AggregateType, @EventType, @EventData, @OccurredAt)
            RETURNING insertedId;"""

        let inline readUnprocessedEvents (DbSchema schema) =
            $"""SELECT "EventId", "CorrelationId", "AggregateId", "AggregateType", "EventData", "OccurredAt", "SuccessfulHandlers"
            FROM "%s{schema}"."EventProcessingLog"
            WHERE "AggregateType" = @AggregateType AND "EventType" = @EventType AND "ProcessedAt" IS NULL
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
        type Event<'eventPayloadDto> =
            { EventId: Guid
              CorrelationId: string
              AggregateId: int
              EventData: 'eventPayloadDto
              OccurredAt: DateTimeOffset
              SuccessfulHandlers: string array }

        module Event =
            let toDomain<'state, 'eventPayloadDto, 'eventPayload>
                (toDomain: 'eventPayloadDto -> Result<'eventPayload, string>)
                dto
                =
                validation {
                    let! payload = dto.EventData |> toDomain
                    and! correlationId = dto.CorrelationId |> CorrelationId.create

                    return
                        dto.AggregateId |> AggregateId.ofInt<'state>,
                        dto.EventId |> EventId,
                        payload |> Event.createExisting dto.OccurredAt correlationId,
                        dto.SuccessfulHandlers |> Set.ofArray
                }
                |> Result.mapError (String.concat "; ")

    type PersistEvents<'state, 'eventPayload> = PersistEvents<'state, EventId, 'eventPayload, SqlIoError>

    let persistEvents
        (eventPayloadToDto: 'eventPayload -> 'eventPayloadDto)
        (dbSchema: DbSchema)
        (dbTransaction: DbTransaction)
        : PersistEvents<'state, 'eventPayload> =
        fun (AggregateId aggregateId) ->
            List.traverseAsyncResultM (fun ev ->
                {| CorrelationId = ev.CorrelationId |> Option.map CorrelationId.value |> Option.toObj
                   AggregateId = aggregateId
                   AggregateType = Aggregate.typeName<'state>
                   EventType = Event.typeName<'eventPayload>
                   EventData = ev.Data |> eventPayloadToDto |> JsonbParameter<'eventPayloadDto>
                   OccurredAt = ev.OccurredAt |}
                |> Dapper.executeScalar<Guid> (SqlSession.Sustained dbTransaction) (Sql.persistEvent dbSchema)
                |> Async.map (
                    Result.bind (fun evId ->
                        result {
                            let! newCorrelationId =
                                ev.CorrelationId
                                |> Option.map Ok
                                |> Option.defaultWith (fun _ -> evId |> string |> CorrelationId.create)
                                |> Result.mapError InvalidData

                            return evId |> EventId, ev.Data |> Event.createExisting ev.OccurredAt newCorrelationId
                        })
                ))

    type ReadUnprocessedEvents<'state, 'eventPayload> =
        ReadUnprocessedEvents<'state, EventId, 'eventPayload, SqlIoError>

    let readUnprocessedEvents
        (dtoToEventPayload: 'eventPayloadDto -> Result<'eventPayload, string>)
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : ReadUnprocessedEvents<'state, 'eventPayload> =
        fun () ->
            {| AggregateType = Aggregate.typeName<'state>
               EventType = Event.typeName<'eventPayload> |}
            |> Dapper.query<Dto.Event<'eventPayloadDto>> sqlSession (Sql.readUnprocessedEvents dbSchema)
            |> AsyncResult.bind (
                Seq.traverseResultA (Dto.Event.toDomain<'state, 'eventPayloadDto, 'eventPayload> dtoToEventPayload)
                >> Result.map Seq.toList
                >> Result.mapError (String.concat "; " >> InvalidData)
                >> AsyncResult.ofResult
            )

    let persistSuccessfulEventHandlers
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : PersistSuccessfulEventHandlers<EventId, SqlIoError> =
        fun (EventId eventId) successfulHandlers ->
            {| EventId = eventId
               SuccessfulHandlers = successfulHandlers |> Set.toArray |}
            |> Dapper.execute sqlSession (Sql.persistSuccessfulEventHandlers dbSchema)
            |> AsyncResult.ignore

    let markEventAsProcessed
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        (getNow: GetUtcNow)
        : MarkEventAsProcessed<EventId, SqlIoError> =
        fun (EventId eventId) ->
            {| EventId = eventId
               UtcNow = getNow () |}
            |> Dapper.execute sqlSession (Sql.markEventAsProcessed dbSchema)
            |> AsyncResult.ignore
