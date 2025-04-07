namespace eShop.DomainDrivenDesign.Postgres

open System
open System.Data.Common
open System.Text.Json
open FsToolkit.ErrorHandling
open eShop.DomainDrivenDesign
open eShop.Postgres
open eShop.Postgres.Dapper.Parameters


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
            INSERT INTO "%s{schema}"."EventProcessingLog" 
            ("EventId", "AggregateId", "AggregateType", "EventType", "EventData", "OccurredAt")
            VALUES (@EventId, @AggregateId, @AggregateType, @EventType, @EventData, @OccurredAt);"""

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
              AggregateId: int
              EventData: 'eventPayloadDto
              OccurredAt: DateTimeOffset
              SuccessfulHandlers: string array }

        module Event =
            let toDomain<'state, 'eventPayloadDto, 'eventPayload>
                (payloadToDomain: 'eventPayloadDto -> Result<'eventPayload, string>)
                dto
                =
                dto.EventData
                |> payloadToDomain
                |> Result.map (fun payload ->
                    dto.AggregateId |> AggregateId.ofInt<'state>,
                    { Id = dto.EventId |> EventId.ofGuid
                      Data = payload
                      OccurredAt = dto.OccurredAt },
                    dto.SuccessfulHandlers |> Set.ofArray)

    type PersistEvents<'state, 'eventPayload> = PersistEvents<'state, 'eventPayload, SqlIoError>

    let persistEvents
        (eventPayloadToDto: 'eventPayload -> 'eventPayloadDto)
        (dbSchema: DbSchema)
        (dbTransaction: DbTransaction)
        : PersistEvents<'state, 'eventPayload> =
        fun (AggregateId aggregateId) ->
            List.map (fun ev ->
                {| EventId = ev.Id |> EventId.value
                   AggregateId = aggregateId
                   AggregateType = Aggregate.typeName<'state>
                   EventType = Event.typeName<'eventPayload>
                   EventData = ev.Data |> eventPayloadToDto |> JsonbParameter<'eventPayloadDto>
                   OccurredAt = ev.OccurredAt |})
            >> Dapper.execute (SqlSession.Sustained dbTransaction) (Sql.persistEvent dbSchema)
            >> AsyncResult.ignore

    type ReadUnprocessedEvents<'state, 'eventPayload> = ReadUnprocessedEvents<'state, 'eventPayload, SqlIoError>

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

    type PersistSuccessfulEventHandlers = PersistSuccessfulEventHandlers<SqlIoError>

    let persistSuccessfulEventHandlers (dbSchema: DbSchema) (sqlSession: SqlSession) : PersistSuccessfulEventHandlers =
        fun eventId successfulHandlers ->
            {| EventId = eventId |> EventId.value
               SuccessfulHandlers = successfulHandlers |> Set.toArray |}
            |> Dapper.execute sqlSession (Sql.persistSuccessfulEventHandlers dbSchema)
            |> AsyncResult.ignore

    type MarkEventAsProcessed = MarkEventAsProcessed<SqlIoError>

    let markEventAsProcessed (dbSchema: DbSchema) (sqlSession: SqlSession) (getNow: GetUtcNow) : MarkEventAsProcessed =
        fun eventId ->
            {| EventId = eventId |> EventId.value
               UtcNow = getNow () |}
            |> Dapper.execute sqlSession (Sql.markEventAsProcessed dbSchema)
            |> AsyncResult.ignore
