namespace Grace.Operations.Data

open System.Data
open System.Threading
open Microsoft.Data.SqlClient
open NodaTime.Text
open Grace.Types.Usage
open Grace.Types.UsageObservation

/// Stores completed TextContent readings separately from additive usage facts.
module TextContentSizeObservations =
    /// Adds only the source-specific immutable table to the existing worker bootstrap.
    let CreateTable =
        """
IF OBJECT_ID(N'ops.TextContentSizeObservation', N'U') IS NULL
CREATE TABLE ops.TextContentSizeObservation (
    ObservationId uniqueidentifier NOT NULL PRIMARY KEY,
    OwnerId uniqueidentifier NOT NULL,
    OrganizationId uniqueidentifier NOT NULL,
    RepositoryId uniqueidentifier NOT NULL,
    DeclaredTextContentUtf8Bytes bigint NOT NULL CHECK (DeclaredTextContentUtf8Bytes >= 0),
    DistinctTextContentCount bigint NOT NULL CHECK (DistinctTextContentCount >= 0),
    EnumerationStartedAt varchar(40) NOT NULL,
    EnumerationFinishedAt varchar(40) NOT NULL
);
"""

    /// Reads the stored row with an optional acceptance lock, preserving every Instant fractional digit.
    let private read (connection: SqlConnection) (transaction: SqlTransaction) locked observationId (token: CancellationToken) =
        task {
            let hint = if locked then "WITH (UPDLOCK,HOLDLOCK) " else ""

            use command =
                new SqlCommand(
                    "SELECT OwnerId,OrganizationId,RepositoryId,DeclaredTextContentUtf8Bytes,DistinctTextContentCount,EnumerationStartedAt,EnumerationFinishedAt FROM ops.TextContentSizeObservation "
                    + hint
                    + "WHERE ObservationId=@id",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("@id", observationId)
            |> ignore

            use! reader = command.ExecuteReaderAsync(token)
            let! exists = reader.ReadAsync(token)

            return
                if exists then
                    Some
                        {
                            ObservationId = observationId
                            Scope = { OwnerId = reader.GetGuid 0; OrganizationId = reader.GetGuid 1; RepositoryId = reader.GetGuid 2 }
                            DeclaredTextContentUtf8Bytes = reader.GetInt64 3
                            DistinctTextContentCount = reader.GetInt64 4
                            EnumerationStartedAt =
                                InstantPattern
                                    .ExtendedIso
                                    .Parse(
                                        reader.GetString 5
                                    )
                                    .Value
                            EnumerationFinishedAt =
                                InstantPattern
                                    .ExtendedIso
                                    .Parse(
                                        reader.GetString 6
                                    )
                                    .Value
                        }
                else
                    None
        }

    /// Checks all requested scope IDs without returning a conflicting stored payload.
    let private matchScope (scope: UsageFactScope) (value: TextContentSizeObservation option) =
        match value with
        | Some stored when stored.Scope <> scope -> Error "ObservationId is already bound to another scope."
        | _ -> Ok value

    /// Resolves a prior completed observation before any repository access or source enumeration.
    let lookup connectionString observationId scope token =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(token)
            let! value = read connection null false observationId token
            return matchScope scope value
        }

    /// Commits the first completed row or returns the existing winner; no lock spans source collection.
    let accept connectionString (candidate: TextContentSizeObservation) token =
        task {
            match TextContentSizeObservation.Validate candidate with
            | Error errors -> return invalidArg (nameof candidate) (String.concat " " errors)
            | Ok () ->
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync(token)
                use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
                let! existing = read connection transaction true candidate.ObservationId token

                let! result =
                    task {
                        match matchScope candidate.Scope existing with
                        | Error message -> return Error message
                        | Ok (Some winner) -> return Ok winner
                        | Ok None ->
                            use command =
                                new SqlCommand(
                                    "INSERT ops.TextContentSizeObservation (ObservationId,OwnerId,OrganizationId,RepositoryId,DeclaredTextContentUtf8Bytes,DistinctTextContentCount,EnumerationStartedAt,EnumerationFinishedAt) VALUES (@id,@owner,@organization,@repository,@bytes,@count,@started,@finished)",
                                    connection,
                                    transaction
                                )

                            [
                                "@id", box candidate.ObservationId
                                "@owner", box candidate.Scope.OwnerId
                                "@organization", box candidate.Scope.OrganizationId
                                "@repository", box candidate.Scope.RepositoryId
                                "@bytes", box candidate.DeclaredTextContentUtf8Bytes
                                "@count", box candidate.DistinctTextContentCount
                                "@started", box (InstantPattern.ExtendedIso.Format candidate.EnumerationStartedAt)
                                "@finished", box (InstantPattern.ExtendedIso.Format candidate.EnumerationFinishedAt)
                            ]
                            |> List.iter (fun (name, value) ->
                                command.Parameters.AddWithValue(name, value)
                                |> ignore)

                            let! _ = command.ExecuteNonQueryAsync(token)
                            let! stored = read connection transaction false candidate.ObservationId token
                            return Ok stored.Value
                    }

                do! transaction.CommitAsync(token)
                return result
        }
