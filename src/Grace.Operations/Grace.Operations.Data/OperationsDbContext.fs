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

        rawFact
            .HasIndex([| "RehydrationExpiresAtUtc" |])
            .HasDatabaseName(OperationsUsageSql.TemporaryHotCleanupExpiryIndexName)
            .HasFilter("[RehydrationExpiresAtUtc] IS NOT NULL")
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

        let pricingPlan = modelBuilder.Entity<PricingPlanEntity>()

        pricingPlan.ToTable(OperationsPricingSql.PricingPlanTableName, OperationsUsageSql.SchemaName)
        |> ignore

        pricingPlan
            .HasKey([| "PricingPlanId" |])
            .HasName("PK_ops_PricingPlan")
        |> ignore

        pricingPlan
            .Property<System.Guid>("PricingPlanId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        pricingPlan
            .Property<string>("PlanCode")
            .HasMaxLength(OperationsPricingSql.PlanCodeMaxLength)
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<string>("DisplayName")
            .HasMaxLength(OperationsPricingSql.DisplayNameMaxLength)
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        pricingPlan
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        pricingPlan
            .HasIndex([| "PlanCode"; "EffectiveFromUtc" |])
            .HasDatabaseName("UX_ops_PricingPlan_CodeEffectiveFrom")
            .IsUnique()
        |> ignore

        let mapping = modelBuilder.Entity<BillableUsageKindMappingEntity>()

        mapping.ToTable(OperationsPricingSql.BillableUsageKindMappingTableName, OperationsUsageSql.SchemaName)
        |> ignore

        mapping
            .HasKey([| "BillableUsageKindMappingId" |])
            .HasName("PK_ops_BillableUsageKindMapping")
        |> ignore

        mapping
            .Property<System.Guid>("BillableUsageKindMappingId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        mapping.Property<int>("FactKind").IsRequired()
        |> ignore

        mapping
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        mapping
            .Property<string>("DisplayName")
            .HasMaxLength(OperationsPricingSql.DisplayNameMaxLength)
            .IsRequired()
        |> ignore

        mapping
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        mapping
            .Property<Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        mapping
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        mapping
            .HasIndex([| "FactKind"; "EffectiveFromUtc" |])
            .HasDatabaseName("UX_ops_BillableUsageKindMapping_FactKindEffectiveFrom")
            .IsUnique()
        |> ignore

        mapping
            .HasIndex(
                [|
                    "FactKind"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName(OperationsPricingSql.BillableUsageKindMappingEffectiveIndexName)
        |> ignore

        let pricingRate = modelBuilder.Entity<PricingRateEntity>()

        pricingRate.ToTable(OperationsPricingSql.PricingRateTableName, OperationsUsageSql.SchemaName)
        |> ignore

        pricingRate
            .HasKey([| "PricingRateId" |])
            .HasName("PK_ops_PricingRate")
        |> ignore

        pricingRate
            .Property<System.Guid>("PricingRateId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        pricingRate
            .Property<System.Guid>("PricingPlanId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<string>("CurrencyCode")
            .HasColumnType("varchar(3)")
            .HasMaxLength(OperationsPricingSql.CurrencyCodeLength)
            .IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<string>("UnitName")
            .HasMaxLength(OperationsPricingSql.UnitNameMaxLength)
            .IsRequired()
        |> ignore

        pricingRate
            .Property<int64>("UnitQuantity")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<int64>("UnitPriceMicros")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        pricingRate
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        pricingRate
            .HasIndex(
                [|
                    "PricingPlanId"
                    "BillableUsageKind"
                    "EffectiveFromUtc"
                |]
            )
            .HasDatabaseName("UX_ops_PricingRate_PlanUsageKindEffectiveFrom")
            .IsUnique()
        |> ignore

        pricingRate
            .HasIndex(
                [|
                    "PricingPlanId"
                    "BillableUsageKind"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName(OperationsPricingSql.PricingRateEffectiveIndexName)
        |> ignore

        pricingRate
            .HasOne(fun rate -> rate.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_PricingRate_PricingPlan")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let assignment = modelBuilder.Entity<CustomerPricingAssignmentEntity>()

        assignment.ToTable(OperationsPricingSql.CustomerPricingAssignmentTableName, OperationsUsageSql.SchemaName)
        |> ignore

        assignment
            .HasKey([| "CustomerPricingAssignmentId" |])
            .HasName("PK_ops_CustomerPricingAssignment")
        |> ignore

        assignment
            .Property<System.Guid>("CustomerPricingAssignmentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        assignment
            .Property<System.Guid>("CustomerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("OwnerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("OrganizationId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("RepositoryId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("PricingPlanId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        assignment
            .Property<Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        assignment
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        assignment
            .HasIndex([| "PricingPlanId" |])
            .HasDatabaseName(OperationsPricingSql.CustomerPricingAssignmentPricingPlanIndexName)
        |> ignore

        assignment
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "EffectiveFromUtc"
                |]
            )
            .HasDatabaseName("UX_ops_CustomerPricingAssignment_ScopeEffectiveFrom")
            .IsUnique()
        |> ignore

        assignment
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName(OperationsPricingSql.CustomerPricingAssignmentScopeIndexName)
        |> ignore

        assignment
            .HasOne(fun assignment -> assignment.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_CustomerPricingAssignment_PricingPlan")
            .OnDelete(DeleteBehavior.Restrict)
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

    /// Exposes pricing plans for EF migrations and schema inspection.
    [<DefaultValue>]
    val mutable private pricingPlans: DbSet<PricingPlanEntity>

    /// Exposes billable usage-kind mappings for EF migrations and schema inspection.
    [<DefaultValue>]
    val mutable private billableUsageKindMappings: DbSet<BillableUsageKindMappingEntity>

    /// Exposes pricing rates for EF migrations and schema inspection.
    [<DefaultValue>]
    val mutable private pricingRates: DbSet<PricingRateEntity>

    /// Exposes customer pricing assignments for EF migrations and schema inspection.
    [<DefaultValue>]
    val mutable private customerPricingAssignments: DbSet<CustomerPricingAssignmentEntity>

    /// Provides the EF set for immutable usage fact rows.
    member this.RawUsageFacts
        with get () = this.rawUsageFacts
        and set value = this.rawUsageFacts <- value

    /// Provides the EF set for repository resource minute aggregate rows.
    member this.UsageAggregateMinutes
        with get () = this.usageAggregateMinutes
        and set value = this.usageAggregateMinutes <- value

    /// Provides the EF set for effective-dated pricing plans.
    member this.PricingPlans
        with get () = this.pricingPlans
        and set value = this.pricingPlans <- value

    /// Provides the EF set for billable usage-kind mappings.
    member this.BillableUsageKindMappings
        with get () = this.billableUsageKindMappings
        and set value = this.billableUsageKindMappings <- value

    /// Provides the EF set for effective-dated pricing rates.
    member this.PricingRates
        with get () = this.pricingRates
        and set value = this.pricingRates <- value

    /// Provides the EF set for customer pricing assignments.
    member this.CustomerPricingAssignments
        with get () = this.customerPricingAssignments
        and set value = this.customerPricingAssignments <- value

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
