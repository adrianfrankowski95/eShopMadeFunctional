[<RequireQualifiedAccess>]
module eShop.Ordering.Adapters.Postgres.PaymentAdapter

open FsToolkit.ErrorHandling
open eShop.Ordering.Domain.Model.ValueObjects
open eShop.Ordering.Domain.Ports
open eShop.Postgres

module internal Dto =
    [<CLIMutable>]
    type CardType = { Id: int; Name: string }

module private Sql =
    let getSupportedCardTypes (DbSchema schema) =
        $"""
        SELECT "Id", "Name",
        FROM "{schema}"."CardTypes"
        """

type GetSupportedCardTypes = PaymentManagementPort.GetSupportedCardTypes<SqlIoError>

let getSupportedCardTypes dbSchema sqlSession : GetSupportedCardTypes =
    fun () ->
        asyncResult {
            let! cardTypeDtos = Dapper.query<Dto.CardType> sqlSession (Sql.getSupportedCardTypes dbSchema) null

            return!
                cardTypeDtos
                |> Seq.traverseResultA (fun dto ->
                    dto.Name
                    |> CardTypeName.create
                    |> Result.map (fun name -> dto.Id |> CardTypeId.ofInt, name))
                |> Result.map (Map.ofSeq >> SupportedCardTypes)
                |> Result.mapError (String.concat "; " >> InvalidData)
        }
