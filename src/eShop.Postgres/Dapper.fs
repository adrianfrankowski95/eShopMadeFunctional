[<RequireQualifiedAccess>]
module eShop.Postgres.Dapper

open System
open Dapper
open FsToolkit.ErrorHandling
open Npgsql
open NpgsqlTypes

let private (|SqlSession|) =
    function
    | SqlSession.Sustained dbTransaction -> (dbTransaction.Connection, dbTransaction)
    | SqlSession.Standalone getDbConnection -> (getDbConnection (), null)

let inline private toAsyncResult f =
    try
        f () |> Async.AwaitTask |> Async.map Ok
    with e ->
        e |> SqlException |> AsyncResult.error

let query<'t> (SqlSession(dbConnection, transaction)) sql param =
    (fun () -> dbConnection.QueryAsync<'t>(sql, param, transaction))
    |> toAsyncResult

let execute (SqlSession(dbConnection, transaction)) sql param =
    (fun () -> dbConnection.ExecuteAsync(sql, param, transaction)) |> toAsyncResult

let executeScalar<'t> (SqlSession(dbConnection, transaction)) sql param =
    (fun () -> dbConnection.ExecuteScalarAsync<'t>(sql, param, transaction))
    |> toAsyncResult

[<RequireQualifiedAccess>]
module TypeHandlers =
    type OptionHandler<'t>() =
        inherit SqlMapper.TypeHandler<option<'t>>()

        override _.SetValue(param, value) =
            let valueOrNull =
                match value with
                | Some x -> box x
                | None -> null

            param.Value <- valueOrNull

        override _.Parse value =
            if isNull value || value = box DBNull.Value then
                None
            else
                Some(value :?> 't)

    let register () =
        SqlMapper.AddTypeHandler(OptionHandler<Guid>())
        SqlMapper.AddTypeHandler(OptionHandler<byte>())
        SqlMapper.AddTypeHandler(OptionHandler<int16>())
        SqlMapper.AddTypeHandler(OptionHandler<int>())
        SqlMapper.AddTypeHandler(OptionHandler<int64>())
        SqlMapper.AddTypeHandler(OptionHandler<uint16>())
        SqlMapper.AddTypeHandler(OptionHandler<uint>())
        SqlMapper.AddTypeHandler(OptionHandler<uint64>())
        SqlMapper.AddTypeHandler(OptionHandler<float>())
        SqlMapper.AddTypeHandler(OptionHandler<decimal>())
        SqlMapper.AddTypeHandler(OptionHandler<float32>())
        SqlMapper.AddTypeHandler(OptionHandler<string>())
        SqlMapper.AddTypeHandler(OptionHandler<char>())
        SqlMapper.AddTypeHandler(OptionHandler<DateTime>())
        SqlMapper.AddTypeHandler(OptionHandler<DateTimeOffset>())
        SqlMapper.AddTypeHandler(OptionHandler<bool>())
        SqlMapper.AddTypeHandler(OptionHandler<TimeSpan>())
        SqlMapper.AddTypeHandler(OptionHandler<byte[]>())

module Parameters =
    type JsonbParameter<'t>(value: 't) =

        interface SqlMapper.ICustomQueryParameter with
            member this.AddParameter(command, name) =
                let parameter = NpgsqlParameter<'t>()
                parameter.TypedValue <- value
                parameter.NpgsqlDbType <- NpgsqlDbType.Jsonb
                parameter.ParameterName <- name

                command.Parameters.Add(parameter) |> ignore
