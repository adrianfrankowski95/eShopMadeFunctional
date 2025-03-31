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
    type EventId = private EventId of Guid

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
            $"""INSERT INTO "%s{schema}"."EventProcessingLog" 
            ("AggregateId", "AggregateType", "EventType", "EventData", "OccurredAt")
            VALUES (@AggregateId, @AggregateType, @EventType, @EventData, @OccurredAt)
            RETURNING "EventId";"""

        let inline readUnprocessedEvents (DbSchema schema) =
            $"""SELECT "EventId", "AggregateId", "AggregateType", "EventData", "OccurredAt", "SuccessfulHandlers"
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
                (toDomain: 'eventPayloadDto -> Result<'eventPayload, string>)
                dto
                =
                dto.EventData
                |> toDomain
                |> Result.map (fun evData ->
                    dto.AggregateId |> AggregateId.ofInt<'state>,
                    dto.EventId |> EventId,
                    { Data = evData
                      OccurredAt = dto.OccurredAt },
                    dto.SuccessfulHandlers |> Set.ofArray)

    let private getTypeName<'t> () =
        typeof<'t>.DeclaringType.Name + typeof<'t>.Name

    type PersistEvent<'state, 'eventPayload> = PersistEvent<'state, EventId, 'eventPayload, SqlIoError>

    let persistEvent
        (eventPayloadToDto: 'eventPayload -> 'eventPayloadDto)
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : PersistEvent<'state, 'eventPayload> =
        fun (AggregateId aggregateId) (ev: Event<'eventPayload>) ->
            {| AggregateId = aggregateId
               AggregateType = getTypeName<'state> ()
               EventType = getTypeName<'eventPayload> ()
               EventData = ev.Data |> eventPayloadToDto |> JsonbParameter<'eventPayloadDto>
               OccurredAt = ev.OccurredAt |}
            |> Dapper.executeScalar<Guid> sqlSession (Sql.persistEvent dbSchema)
            |> AsyncResult.map (fun evId -> evId |> EventId, ev)

    type PersistEvents<'state, 'eventPayload> = PersistEvents<'state, EventId, 'eventPayload, SqlIoError>

    let persistEvents
        (eventPayloadToDto: 'eventPayload -> 'eventPayloadDto)
        (dbSchema: DbSchema)
        (dbTransaction: DbTransaction)
        : PersistEvents<'state, 'eventPayload> =
        fun (AggregateId aggregateId) ->
            List.traverseAsyncResultM (fun ev ->
                {| AggregateId = aggregateId
                   AggregateType = getTypeName<'state> ()
                   EventType = getTypeName<'eventPayload> ()
                   EventData = ev.Data |> eventPayloadToDto |> JsonbParameter<'eventPayloadDto>
                   OccurredAt = ev.OccurredAt |}
                |> Dapper.executeScalar<Guid> (SqlSession.Sustained dbTransaction) (Sql.persistEvent dbSchema)
                |> AsyncResult.map (fun evId -> evId |> EventId, ev))

    type ReadUnprocessedEvents<'state, 'eventPayload> =
        ReadUnprocessedEvents<'state, EventId, 'eventPayload, SqlIoError>

    let readUnprocessedEvents
        (dtoToEventPayload: 'eventPayloadDto -> Result<'eventPayload, string>)
        (dbSchema: DbSchema)
        (sqlSession: SqlSession)
        : ReadUnprocessedEvents<'state, 'eventPayload> =
        fun () ->
            {| AggregateType = getTypeName<'state> ()
               EventType = getTypeName<'eventPayload> () |}
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
