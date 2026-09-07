namespace Grace.Operations.Data

open System.Data
open System.Threading
open Microsoft.Data.SqlClient
open NodaTime.Text
open Grace.Types.Usage
open Grace.Types.UsageObservation

/// Stores completed Artifact readings separately from additive usage facts.
module ArtifactSizeObservations =
    /// Adds only the source-specific immutable table to the existing worker bootstrap.
    let CreateTable =
        """
IF OBJECT_ID(N'ops.ArtifactSizeObservation', N'U') IS NULL
CREATE TABLE ops.ArtifactSizeObservation (
    ObservationId uniqueidentifier NOT NULL PRIMARY KEY,
    OwnerId uniqueidentifier NOT NULL,
    OrganizationId uniqueidentifier NOT NULL,
    RepositoryId uniqueidentifier NOT NULL,
    DeclaredArtifactBytes bigint NOT NULL CHECK (DeclaredArtifactBytes >= 0),
    DistinctArtifactCount bigint NOT NULL CHECK (DistinctArtifactCount >= 0),
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
                    "SELECT OwnerId,OrganizationId,RepositoryId,DeclaredArtifactBytes,DistinctArtifactCount,EnumerationStartedAt,EnumerationFinishedAt FROM ops.ArtifactSizeObservation "
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
                            DeclaredArtifactBytes = reader.GetInt64 3
                            DistinctArtifactCount = reader.GetInt64 4
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
    let private matchScope (scope: UsageFactScope) (value: ArtifactSizeObservation option) =
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
    let accept connectionString (candidate: ArtifactSizeObservation) token =
        task {
            match ArtifactSizeObservation.Validate candidate with
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
                                    "INSERT ops.ArtifactSizeObservation (ObservationId,OwnerId,OrganizationId,RepositoryId,DeclaredArtifactBytes,DistinctArtifactCount,EnumerationStartedAt,EnumerationFinishedAt) VALUES (@id,@owner,@organization,@repository,@bytes,@count,@started,@finished)",
                                    connection,
                                    transaction
                                )

                            [
                                "@id", box candidate.ObservationId
                                "@owner", box candidate.Scope.OwnerId
                                "@organization", box candidate.Scope.OrganizationId
                                "@repository", box candidate.Scope.RepositoryId
                                "@bytes", box candidate.DeclaredArtifactBytes
                                "@count", box candidate.DistinctArtifactCount
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
