namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations

/// Creates the baseline Operations SQL Server schema for raw usage facts and minute aggregates.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260705234500_InitialOperationsSchema")>]
type InitialOperationsSchema() =
    inherit Migration()

    /// Applies the first reviewed Operations SQL Server schema.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.EnsureSchema(OperationsUsageSql.SchemaName)
        |> ignore

        migrationBuilder.Sql(
            """
IF OBJECT_ID(N'ops.RawUsageFact', N'U') IS NULL
BEGIN
    CREATE TABLE ops.RawUsageFact
    (
        UsageFactId uniqueidentifier NOT NULL,
        CorrelationId nvarchar(200) NOT NULL,
        FactKind int NOT NULL,
        OwnerId uniqueidentifier NOT NULL,
        OrganizationId uniqueidentifier NOT NULL,
        RepositoryId uniqueidentifier NOT NULL,
        StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Quantity bigint NOT NULL,
        ObservedAtUtc datetime2(7) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_RawUsageFact_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_RawUsageFact PRIMARY KEY CLUSTERED (UsageFactId)
    );
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            """
IF OBJECT_ID(N'ops.UsageAggregateMinute', N'U') IS NULL
BEGIN
    CREATE TABLE ops.UsageAggregateMinute
    (
        FactKind int NOT NULL,
        OwnerId uniqueidentifier NOT NULL,
        OrganizationId uniqueidentifier NOT NULL,
        RepositoryId uniqueidentifier NOT NULL,
        StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        BucketStartUtc datetime2(7) NOT NULL,
        Quantity bigint NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_UsageAggregateMinute_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_UsageAggregateMinute PRIMARY KEY CLUSTERED
        (
            FactKind,
            OwnerId,
            OrganizationId,
            RepositoryId,
            StoragePoolId,
            BucketStartUtc
        )
    );
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            """
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_ops_RawUsageFact_ScopeKindObservedAt'
      AND object_id = OBJECT_ID(N'ops.RawUsageFact')
)
BEGIN
    CREATE INDEX IX_ops_RawUsageFact_ScopeKindObservedAt
    ON ops.RawUsageFact (OwnerId, OrganizationId, RepositoryId, FactKind, ObservedAtUtc);
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            """
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_ops_UsageAggregateMinute_ScopeKindBucket'
      AND object_id = OBJECT_ID(N'ops.UsageAggregateMinute')
)
BEGIN
    CREATE INDEX IX_ops_UsageAggregateMinute_ScopeKindBucket
    ON ops.UsageAggregateMinute (OwnerId, OrganizationId, RepositoryId, FactKind, BucketStartUtc);
END;
"""
        )
        |> ignore

    /// Removes the baseline Operations schema objects in reverse dependency order.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql("IF OBJECT_ID(N'ops.UsageAggregateMinute', N'U') IS NOT NULL DROP TABLE ops.UsageAggregateMinute;")
        |> ignore

        migrationBuilder.Sql("IF OBJECT_ID(N'ops.RawUsageFact', N'U') IS NOT NULL DROP TABLE ops.RawUsageFact;")
        |> ignore

    /// Captures the raw fact and aggregate model that future migrations diff against.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        // Deliberately keep this migration target model frozen with literals so future runtime model edits
        // cannot rewrite the reviewed baseline migration point.
        modelBuilder.HasDefaultSchema("ops") |> ignore

        let rawFact = modelBuilder.Entity<RawUsageFactEntity>()

        rawFact.ToTable("RawUsageFact", "ops") |> ignore

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
            .HasMaxLength(200)
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
            .HasMaxLength(256)
            .UseCollation("Latin1_General_100_BIN2")
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

        aggregate.ToTable("UsageAggregateMinute", "ops")
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
            .HasMaxLength(256)
            .UseCollation("Latin1_General_100_BIN2")
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
