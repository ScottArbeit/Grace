namespace Grace.Operations.Data

open Microsoft.Data.SqlClient
open System
open System.Data
open System.Threading

/// Identifies the Grace operations data assembly until the daily usage ledger adds its persistence contracts.
[<AbstractClass; Sealed>]
type internal OperationsDataAssembly =
    class
    end

/// Chooses whether schema initialization may connect to `master` to create the operations database.
type OperationsUsageSchemaBootstrapMode =

    /// Opens only the configured target database, which supports least-privilege users without `master` access.
    | TargetDatabaseOnly = 1

    /// Opens `master` first to create the configured target database when an admin bootstrap caller opts in.
    | CreateDatabaseIfMissing = 2

/// Describes the SQL connections needed for an operations usage schema initialization pass.
type internal OperationsUsageSchemaBootstrapPlan =
    {
        TargetDatabaseName: string option
        SchemaConnectionString: string
        DatabaseCreationConnectionString: string option
    }

/// Builds schema initialization plans without opening SQL connections.
[<RequireQualifiedAccess>]
module internal OperationsUsageSchemaBootstrapPlan =

    /// Creates a plan that uses the target database by default and connects to `master` only for explicit bootstrap.
    let create connectionString mode =
        let builder = SqlConnectionStringBuilder(connectionString)

        let targetDatabaseName =
            if String.IsNullOrWhiteSpace builder.InitialCatalog then
                None
            else
                Some builder.InitialCatalog

        let databaseCreationConnectionString =
            match mode, targetDatabaseName with
            | OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing, Some _ ->
                let masterBuilder = SqlConnectionStringBuilder(connectionString)
                masterBuilder.InitialCatalog <- "master"
                Some masterBuilder.ConnectionString
            | _ -> None

        {
            TargetDatabaseName = targetDatabaseName
            SchemaConnectionString = connectionString
            DatabaseCreationConnectionString = databaseCreationConnectionString
        }

/// Provides SQL Server command text for the operations database and its schema.
[<RequireQualifiedAccess>]
module OperationsUsageSql =

    /// Names the SQL schema used by Grace operations data.
    [<Literal>]
    let SchemaName = "ops"

    /// Creates the operations schema when it is absent.
    [<Literal>]
    let CreateSchema = "IF SCHEMA_ID(N'ops') IS NULL EXEC(N'CREATE SCHEMA ops');"

    /// Creates the configured operations database when SQL Server does not already contain it.
    [<Literal>]
    let CreateDatabaseIfMissing =
        """
IF DB_ID(@DatabaseName) IS NULL
BEGIN
    DECLARE @CreateDatabaseSql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
    EXEC(@CreateDatabaseSql);
END;
"""

/// Ensures the operations database and its `ops` schema exist before a caller writes operations data.
type OperationsUsageSchema(connectionString: string, ?bootstrapMode: OperationsUsageSchemaBootstrapMode) =

    /// Describes whether this schema pass is target-only or explicit admin bootstrap.
    let bootstrapMode = defaultArg bootstrapMode OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly

    /// Captures the connection strings used by this schema pass without opening SQL connections.
    let bootstrapPlan = OperationsUsageSchemaBootstrapPlan.create connectionString bootstrapMode

    /// Opens the SQL connection used for one schema initialization pass.
    let openConnectionAsync databaseConnectionString cancellationToken =
        task {
            let connection = new SqlConnection(databaseConnectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Executes a schema command against the operations database.
    let executeCommandAsync (connection: SqlConnection) commandText cancellationToken =
        task {
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- commandText
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Creates the configured operations database when the connection string points at a database that is absent.
    let ensureDatabaseCreatedAsync cancellationToken =
        task {
            match bootstrapPlan.DatabaseCreationConnectionString, bootstrapPlan.TargetDatabaseName with
            | Some databaseCreationConnectionString, Some databaseName ->
                use! connection = openConnectionAsync databaseCreationConnectionString cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- OperationsUsageSql.CreateDatabaseIfMissing

                let parameter = command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128)
                parameter.Value <- databaseName
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            | _ -> return ()
        }

    /// Creates the operations database (in explicit bootstrap mode) and the `ops` schema when they are absent.
    member _.EnsureCreatedAsync(cancellationToken: CancellationToken) =
        task {
            do! ensureDatabaseCreatedAsync cancellationToken
            use! connection = openConnectionAsync bootstrapPlan.SchemaConnectionString cancellationToken
            do! executeCommandAsync connection OperationsUsageSql.CreateSchema cancellationToken
        }
