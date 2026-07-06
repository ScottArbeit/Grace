namespace Grace.Operations.Data

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure

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

/// Builds configured Operations EF contexts for runtime schema migration and tests.
[<RequireQualifiedAccess>]
module OperationsDbContextFactory =

    /// Creates EF Core options for the Operations SQL Server database.
    let options (connectionString: string) =
        DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(
            connectionString,
            System.Action<SqlServerDbContextOptionsBuilder> (fun sql ->
                sql.MigrationsHistoryTable("__EFMigrationsHistory", OperationsUsageSql.SchemaName)
                |> ignore)
        )
            .Options

    /// Creates a configured Operations EF context for the supplied SQL Server connection string.
    let create connectionString = new OperationsDbContext(options connectionString)
