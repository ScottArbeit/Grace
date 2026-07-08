namespace Grace.Operations.Data

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Design
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.SqlServer.Migrations.Internal
open System

/// Configures the Operations EF Core model without opening a database connection.
[<RequireQualifiedAccess>]
module OperationsModel =

    /// Configures the raw fact and minute aggregate tables that form the Operations usage foundation.
    let configure (modelBuilder: ModelBuilder) =
        modelBuilder.HasDefaultSchema(OperationsUsageSql.SchemaName)
        |> ignore

        let rawFact = modelBuilder.Entity<RawUsageFactEntity>()

        rawFact.ToTable(OperationsUsageSql.RawUsageFactTableName, OperationsUsageSql.SchemaName)
        |> ignore

        rawFact
            .HasKey([| "UsageFactId" |])
            .HasName("PK_ops_RawUsageFact")
        |> ignore

        rawFact
            .Property<System.Guid>("UsageFactId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        rawFact
            .Property<byte array>("RawPayload")
            .HasColumnType("varbinary(max)")
        |> ignore

        rawFact
            .Property<string>("CorrelationId")
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
            .IsRequired()
        |> ignore

        rawFact.Property<int>("FactKind").IsRequired()
        |> ignore

        rawFact
            .Property<System.Guid>("OwnerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        rawFact
            .Property<System.Guid>("OrganizationId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        rawFact
            .Property<System.Guid>("RepositoryId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        rawFact
            .Property<string>("StoragePoolId")
            .HasMaxLength(OperationsUsageSql.StoragePoolIdMaxLength)
            .UseCollation(OperationsUsageSql.CaseSensitiveStoragePoolIdCollation)
            .IsRequired()
        |> ignore

        rawFact.Property<int64>("Quantity").IsRequired()
        |> ignore

        rawFact
            .Property<System.DateTime>("ObservedAtUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        rawFact.Property<int>("ArchiveState").IsRequired()
        |> ignore

        rawFact
            .Property<string>("ArchiveBlobName")
            .HasMaxLength(OperationsUsageSql.ArchiveBlobNameMaxLength)
        |> ignore

        rawFact
            .Property<string>("ArchiveChecksumSha256Hex")
            .HasMaxLength(OperationsUsageSql.ArchiveChecksumSha256HexLength)
            .IsFixedLength()
            .IsUnicode(false)
        |> ignore

        rawFact
            .Property<Nullable<int64>>("ArchiveByteLength")
            .HasColumnType("bigint")
        |> ignore

        rawFact
            .Property<Nullable<System.DateTime>>("ArchiveVerifiedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<Nullable<System.DateTime>>("ArchivedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<Nullable<System.DateTime>>("RehydrationExpiresAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<string>("LastArchiveFailureReason")
            .HasMaxLength(OperationsUsageSql.ArchiveFailureReasonMaxLength)
        |> ignore

        rawFact
            .Property<Nullable<System.DateTime>>("LastArchiveFailureAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<int>("ArchiveFailureCount")
            .IsRequired()
        |> ignore

        rawFact
            .Property<Nullable<System.DateTime>>("ArchiveRetiredAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        rawFact
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "FactKind"
                    "ObservedAtUtc"
                |]
            )
            .HasDatabaseName("IX_ops_RawUsageFact_ScopeKindObservedAt")
        |> ignore

        rawFact
            .HasIndex(
                [|
                    "ArchiveState"
                    "ObservedAtUtc"
                    "UsageFactId"
                |]
            )
            .HasDatabaseName("IX_ops_RawUsageFact_ArchiveStateObservedAt")
        |> ignore

        let aggregate = modelBuilder.Entity<UsageAggregateMinuteEntity>()

        aggregate.ToTable(OperationsUsageSql.UsageAggregateMinuteTableName, OperationsUsageSql.SchemaName)
        |> ignore

        aggregate
            .HasKey(
                [|
                    "FactKind"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "StoragePoolId"
                    "BucketStartUtc"
                |]
            )
            .HasName("PK_ops_UsageAggregateMinute")
        |> ignore

        aggregate.Property<int>("FactKind").IsRequired()
        |> ignore

        aggregate
            .Property<System.Guid>("OwnerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        aggregate
            .Property<System.Guid>("OrganizationId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        aggregate
            .Property<System.Guid>("RepositoryId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        aggregate
            .Property<string>("StoragePoolId")
            .HasMaxLength(OperationsUsageSql.StoragePoolIdMaxLength)
            .UseCollation(OperationsUsageSql.CaseSensitiveStoragePoolIdCollation)
            .IsRequired()
        |> ignore

        aggregate
            .Property<System.DateTime>("BucketStartUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        aggregate.Property<int64>("Quantity").IsRequired()
        |> ignore

        aggregate
            .Property<System.DateTime>("UpdatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        aggregate
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "FactKind"
                    "BucketStartUtc"
                |]
            )
            .HasDatabaseName("IX_ops_UsageAggregateMinute_ScopeKindBucket")
        |> ignore

/// Owns the EF Core model for Grace Operations SQL Server schema evolution.
type OperationsDbContext(options: DbContextOptions<OperationsDbContext>) =
    inherit DbContext(options)

    /// Exposes immutable usage facts for EF migrations and schema inspection.
    [<DefaultValue>]
    val mutable private rawUsageFacts: DbSet<RawUsageFactEntity>

    /// Exposes UTC minute aggregates for EF migrations and schema inspection.
    [<DefaultValue>]
    val mutable private usageAggregateMinutes: DbSet<UsageAggregateMinuteEntity>

    /// Provides the EF set for immutable usage fact rows.
    member this.RawUsageFacts
        with get () = this.rawUsageFacts
        and set value = this.rawUsageFacts <- value

    /// Provides the EF set for repository resource minute aggregate rows.
    member this.UsageAggregateMinutes
        with get () = this.usageAggregateMinutes
        and set value = this.usageAggregateMinutes <- value

    /// Configures the Operations SQL Server schema shape that migrations must preserve.
    override _.OnModelCreating(modelBuilder: ModelBuilder) = OperationsModel.configure modelBuilder

/// Ensures EF-generated scripts create the Operations schema before SQL Server receives history-table DDL.
type OperationsSqlServerHistoryRepository(dependencies: HistoryRepositoryDependencies) =
    inherit SqlServerHistoryRepository(dependencies)

    let withOpsSchemaPreamble (script: string) =
        if String.IsNullOrWhiteSpace script then
            script
        else
            $"{OperationsUsageSql.CreateSchemaIfMissing.Trim()}{Environment.NewLine}{Environment.NewLine}{script}"

    /// Creates the Operations schema before EF creates `[ops].[__EFMigrationsHistory]` in non-idempotent scripts.
    override _.GetCreateScript() = base.GetCreateScript() |> withOpsSchemaPreamble

    /// Creates the Operations schema before EF creates `[ops].[__EFMigrationsHistory]` in idempotent scripts and bundles.
    override _.GetCreateIfNotExistsScript() =
        base.GetCreateIfNotExistsScript()
        |> withOpsSchemaPreamble

/// Builds configured Operations EF contexts for runtime schema migration and tests.
[<RequireQualifiedAccess>]
module OperationsDbContextFactory =

    /// Provides the SQL Server connection string used when EF tooling builds migrations without opening the database.
    let private defaultDesignTimeConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsDesignTime;Integrated Security=true;"

    /// Names the documented Operations SQL environment variable shared with `Grace.Operations.Worker`.
    let private documentedSqlConnectionStringEnvironmentVariable = "grace__operations__sql__connectionstring"

    /// Names the legacy design-time SQL environment variable retained for local developer compatibility.
    let private legacySqlConnectionStringEnvironmentVariable = "GRACE_OPERATIONS_SQL_CONNECTION_STRING"

    /// Reads the first non-empty environment variable from the supplied precedence order.
    let private firstEnvironmentConnectionString variableNames =
        variableNames
        |> Seq.map Environment.GetEnvironmentVariable
        |> Seq.tryFind (fun value -> not (String.IsNullOrWhiteSpace value))

    /// Resolves the design-time SQL Server connection string from EF command arguments, environment, or LocalDB.
    let designTimeConnectionString (args: string array) =
        match args
              |> Array.tryFind (fun value -> not (String.IsNullOrWhiteSpace value))
            with
        | Some connectionString -> connectionString
        | None ->
            [|
                documentedSqlConnectionStringEnvironmentVariable
                legacySqlConnectionStringEnvironmentVariable
            |]
            |> firstEnvironmentConnectionString
            |> Option.defaultValue defaultDesignTimeConnectionString

    /// Creates EF Core options for the Operations SQL Server database.
    let options (connectionString: string) =
        DbContextOptionsBuilder<OperationsDbContext>()
            .ReplaceService<IHistoryRepository, OperationsSqlServerHistoryRepository>()
            .UseSqlServer(
            connectionString,
            System.Action<SqlServerDbContextOptionsBuilder> (fun sql ->
                sql.MigrationsHistoryTable("__EFMigrationsHistory", OperationsUsageSql.SchemaName)
                |> ignore)
        )
            .Options

    /// Creates a configured Operations EF context for the supplied SQL Server connection string.
    let create connectionString = new OperationsDbContext(options connectionString)

/// Provides the discoverable EF Core design-time factory used by `dotnet ef migrations add`.
type OperationsDesignTimeDbContextFactory() =

    interface IDesignTimeDbContextFactory<OperationsDbContext> with

        /// Creates an Operations context without relying on F# module discovery.
        member _.CreateDbContext(args: string array) =
            OperationsDbContextFactory.designTimeConnectionString args
            |> OperationsDbContextFactory.create
