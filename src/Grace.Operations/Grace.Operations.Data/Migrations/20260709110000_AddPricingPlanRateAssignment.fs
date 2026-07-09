namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations

/// Adds effective-dated pricing plans, billable mappings, rates, and customer assignments.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260709110000_AddPricingPlanRateAssignment")>]
type AddPricingPlanRateAssignment() =
    inherit Migration()

    /// Applies the pricing foundation schema required before previews or posted ledgers can exist.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.EnsureSchema(OperationsUsageSql.SchemaName)
        |> ignore

        migrationBuilder.Sql(
            $"""
IF OBJECT_ID(N'{OperationsPricingSql.PricingPlanTable}', N'U') IS NULL
BEGIN
    CREATE TABLE {OperationsPricingSql.PricingPlanTable}
    (
        PricingPlanId uniqueidentifier NOT NULL,
        PlanCode nvarchar({OperationsPricingSql.PlanCodeMaxLength}) NOT NULL,
        DisplayName nvarchar({OperationsPricingSql.DisplayNameMaxLength}) NOT NULL,
        EffectiveFromUtc datetime2(7) NOT NULL,
        EffectiveToUtc datetime2(7) NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_PricingPlan_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_PricingPlan PRIMARY KEY CLUSTERED (PricingPlanId),
        CONSTRAINT CK_ops_PricingPlan_PricingPlanId_NotEmpty CHECK (PricingPlanId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_PricingPlan_PlanCode_NotBlank CHECK (LEN(LTRIM(RTRIM(PlanCode))) > 0),
        CONSTRAINT CK_ops_PricingPlan_DisplayName_NotBlank CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0),
        CONSTRAINT CK_ops_PricingPlan_EffectiveRange CHECK (EffectiveToUtc IS NULL OR EffectiveToUtc > EffectiveFromUtc)
    );
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF OBJECT_ID(N'{OperationsPricingSql.BillableUsageKindMappingTable}', N'U') IS NULL
BEGIN
    CREATE TABLE {OperationsPricingSql.BillableUsageKindMappingTable}
    (
        BillableUsageKindMappingId uniqueidentifier NOT NULL,
        FactKind int NOT NULL,
        BillableUsageKind int NOT NULL,
        DisplayName nvarchar({OperationsPricingSql.DisplayNameMaxLength}) NOT NULL,
        EffectiveFromUtc datetime2(7) NOT NULL,
        EffectiveToUtc datetime2(7) NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillableUsageKindMapping_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_BillableUsageKindMapping PRIMARY KEY CLUSTERED (BillableUsageKindMappingId),
        CONSTRAINT CK_ops_BillableUsageKindMapping_Id_NotEmpty CHECK (BillableUsageKindMappingId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_BillableUsageKindMapping_FactKind_Positive CHECK (FactKind > 0),
        CONSTRAINT CK_ops_BillableUsageKindMapping_BillableUsageKind_Positive CHECK (BillableUsageKind > 0),
        CONSTRAINT CK_ops_BillableUsageKindMapping_DisplayName_NotBlank CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0),
        CONSTRAINT CK_ops_BillableUsageKindMapping_EffectiveRange CHECK (EffectiveToUtc IS NULL OR EffectiveToUtc > EffectiveFromUtc)
    );
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF OBJECT_ID(N'{OperationsPricingSql.PricingRateTable}', N'U') IS NULL
BEGIN
    CREATE TABLE {OperationsPricingSql.PricingRateTable}
    (
        PricingRateId uniqueidentifier NOT NULL,
        PricingPlanId uniqueidentifier NOT NULL,
        BillableUsageKind int NOT NULL,
        CurrencyCode varchar({OperationsPricingSql.CurrencyCodeLength}) COLLATE Latin1_General_100_BIN2 NOT NULL,
        UnitName nvarchar({OperationsPricingSql.UnitNameMaxLength}) NOT NULL,
        UnitQuantity bigint NOT NULL,
        UnitPriceMicros bigint NOT NULL,
        EffectiveFromUtc datetime2(7) NOT NULL,
        EffectiveToUtc datetime2(7) NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_PricingRate_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_PricingRate PRIMARY KEY CLUSTERED (PricingRateId),
        CONSTRAINT FK_ops_PricingRate_PricingPlan FOREIGN KEY (PricingPlanId) REFERENCES {OperationsPricingSql.PricingPlanTable}(PricingPlanId),
        CONSTRAINT CK_ops_PricingRate_Id_NotEmpty CHECK (PricingRateId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_PricingRate_BillableUsageKind_Positive CHECK (BillableUsageKind > 0),
        CONSTRAINT CK_ops_PricingRate_CurrencyCode_Upper CHECK (LEN(CurrencyCode) = 3 AND CurrencyCode COLLATE Latin1_General_100_BIN2 = UPPER(CurrencyCode) COLLATE Latin1_General_100_BIN2 AND CurrencyCode COLLATE Latin1_General_100_BIN2 NOT LIKE '%%[^A-Z]%%'),
        CONSTRAINT CK_ops_PricingRate_UnitName_NotBlank CHECK (LEN(LTRIM(RTRIM(UnitName))) > 0),
        CONSTRAINT CK_ops_PricingRate_UnitQuantity_Positive CHECK (UnitQuantity > 0),
        CONSTRAINT CK_ops_PricingRate_UnitPriceMicros_NonNegative CHECK (UnitPriceMicros >= 0),
        CONSTRAINT CK_ops_PricingRate_EffectiveRange CHECK (EffectiveToUtc IS NULL OR EffectiveToUtc > EffectiveFromUtc)
    );
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF OBJECT_ID(N'{OperationsPricingSql.CustomerPricingAssignmentTable}', N'U') IS NULL
BEGIN
    CREATE TABLE {OperationsPricingSql.CustomerPricingAssignmentTable}
    (
        CustomerPricingAssignmentId uniqueidentifier NOT NULL,
        CustomerId uniqueidentifier NOT NULL,
        OwnerId uniqueidentifier NOT NULL,
        OrganizationId uniqueidentifier NOT NULL,
        RepositoryId uniqueidentifier NOT NULL,
        PricingPlanId uniqueidentifier NOT NULL,
        EffectiveFromUtc datetime2(7) NOT NULL,
        EffectiveToUtc datetime2(7) NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_CustomerPricingAssignment_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_CustomerPricingAssignment PRIMARY KEY CLUSTERED (CustomerPricingAssignmentId),
        CONSTRAINT FK_ops_CustomerPricingAssignment_PricingPlan FOREIGN KEY (PricingPlanId) REFERENCES {OperationsPricingSql.PricingPlanTable}(PricingPlanId),
        CONSTRAINT CK_ops_CustomerPricingAssignment_Id_NotEmpty CHECK (CustomerPricingAssignmentId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_CustomerPricingAssignment_CustomerId_NotEmpty CHECK (CustomerId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_CustomerPricingAssignment_OwnerId_NotEmpty CHECK (OwnerId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_CustomerPricingAssignment_OrganizationId_NotEmpty CHECK (OrganizationId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_CustomerPricingAssignment_RepositoryId_NotEmpty CHECK (RepositoryId <> '00000000-0000-0000-0000-000000000000'),
        CONSTRAINT CK_ops_CustomerPricingAssignment_EffectiveRange CHECK (EffectiveToUtc IS NULL OR EffectiveToUtc > EffectiveFromUtc)
    );
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.PricingPlanTable}')
    AND name = N'UX_ops_PricingPlan_CodeEffectiveFrom'
)
BEGIN
    CREATE UNIQUE INDEX UX_ops_PricingPlan_CodeEffectiveFrom
        ON {OperationsPricingSql.PricingPlanTable}(PlanCode, EffectiveFromUtc);
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.BillableUsageKindMappingTable}')
    AND name = N'UX_ops_BillableUsageKindMapping_FactKindEffectiveFrom'
)
BEGIN
    CREATE UNIQUE INDEX UX_ops_BillableUsageKindMapping_FactKindEffectiveFrom
        ON {OperationsPricingSql.BillableUsageKindMappingTable}(FactKind, EffectiveFromUtc);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.BillableUsageKindMappingTable}')
    AND name = N'{OperationsPricingSql.BillableUsageKindMappingEffectiveIndexName}'
)
BEGIN
    CREATE INDEX {OperationsPricingSql.BillableUsageKindMappingEffectiveIndexName}
        ON {OperationsPricingSql.BillableUsageKindMappingTable}(FactKind, EffectiveFromUtc, EffectiveToUtc);
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.PricingRateTable}')
    AND name = N'UX_ops_PricingRate_PlanUsageKindEffectiveFrom'
)
BEGIN
    CREATE UNIQUE INDEX UX_ops_PricingRate_PlanUsageKindEffectiveFrom
        ON {OperationsPricingSql.PricingRateTable}(PricingPlanId, BillableUsageKind, EffectiveFromUtc);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.PricingRateTable}')
    AND name = N'{OperationsPricingSql.PricingRateEffectiveIndexName}'
)
BEGIN
    CREATE INDEX {OperationsPricingSql.PricingRateEffectiveIndexName}
        ON {OperationsPricingSql.PricingRateTable}(PricingPlanId, BillableUsageKind, EffectiveFromUtc, EffectiveToUtc);
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.CustomerPricingAssignmentTable}')
    AND name = N'{OperationsPricingSql.CustomerPricingAssignmentPricingPlanIndexName}'
)
BEGIN
    CREATE INDEX {OperationsPricingSql.CustomerPricingAssignmentPricingPlanIndexName}
        ON {OperationsPricingSql.CustomerPricingAssignmentTable}(PricingPlanId);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.CustomerPricingAssignmentTable}')
    AND name = N'UX_ops_CustomerPricingAssignment_ScopeEffectiveFrom'
)
BEGIN
    CREATE UNIQUE INDEX UX_ops_CustomerPricingAssignment_ScopeEffectiveFrom
        ON {OperationsPricingSql.CustomerPricingAssignmentTable}(CustomerId, OwnerId, OrganizationId, RepositoryId, EffectiveFromUtc);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsPricingSql.CustomerPricingAssignmentTable}')
    AND name = N'{OperationsPricingSql.CustomerPricingAssignmentScopeIndexName}'
)
BEGIN
    CREATE INDEX {OperationsPricingSql.CustomerPricingAssignmentScopeIndexName}
        ON {OperationsPricingSql.CustomerPricingAssignmentTable}(CustomerId, OwnerId, OrganizationId, RepositoryId, EffectiveFromUtc, EffectiveToUtc);
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
EXEC(N'CREATE OR ALTER TRIGGER ops.{OperationsPricingSql.PricingPlanOverlapTriggerName}
ON {OperationsPricingSql.PricingPlanTable}
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted AS candidate
        INNER JOIN {OperationsPricingSql.PricingPlanTable} AS existing WITH (UPDLOCK, HOLDLOCK)
            ON existing.PlanCode = candidate.PlanCode
            AND existing.PricingPlanId <> candidate.PricingPlanId
            AND candidate.EffectiveFromUtc < ISNULL(existing.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
            AND existing.EffectiveFromUtc < ISNULL(candidate.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
    )
    BEGIN
        THROW 57411, ''Pricing plan effective windows cannot overlap for the same plan code.'', 1;
    END;
END;');
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
EXEC(N'CREATE OR ALTER TRIGGER ops.{OperationsPricingSql.BillableUsageKindMappingOverlapTriggerName}
ON {OperationsPricingSql.BillableUsageKindMappingTable}
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted AS candidate
        INNER JOIN {OperationsPricingSql.BillableUsageKindMappingTable} AS existing WITH (UPDLOCK, HOLDLOCK)
            ON existing.FactKind = candidate.FactKind
            AND existing.BillableUsageKindMappingId <> candidate.BillableUsageKindMappingId
            AND candidate.EffectiveFromUtc < ISNULL(existing.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
            AND existing.EffectiveFromUtc < ISNULL(candidate.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
    )
    BEGIN
        THROW 57412, ''Billable usage-kind mapping effective windows cannot overlap for the same tracked fact kind.'', 1;
    END;
END;');
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
EXEC(N'CREATE OR ALTER TRIGGER ops.{OperationsPricingSql.PricingRateOverlapTriggerName}
ON {OperationsPricingSql.PricingRateTable}
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted AS candidate
        INNER JOIN {OperationsPricingSql.PricingRateTable} AS existing WITH (UPDLOCK, HOLDLOCK)
            ON existing.PricingPlanId = candidate.PricingPlanId
            AND existing.BillableUsageKind = candidate.BillableUsageKind
            AND existing.PricingRateId <> candidate.PricingRateId
            AND candidate.EffectiveFromUtc < ISNULL(existing.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
            AND existing.EffectiveFromUtc < ISNULL(candidate.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
    )
    BEGIN
        THROW 57413, ''Pricing rate effective windows cannot overlap for the same plan and billable usage kind.'', 1;
    END;
END;');
"""
        )
        |> ignore

        migrationBuilder.Sql(
            $"""
EXEC(N'CREATE OR ALTER TRIGGER ops.{OperationsPricingSql.CustomerPricingAssignmentOverlapTriggerName}
ON {OperationsPricingSql.CustomerPricingAssignmentTable}
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted AS candidate
        INNER JOIN {OperationsPricingSql.CustomerPricingAssignmentTable} AS existing WITH (UPDLOCK, HOLDLOCK)
            ON existing.CustomerId = candidate.CustomerId
            AND existing.OwnerId = candidate.OwnerId
            AND existing.OrganizationId = candidate.OrganizationId
            AND existing.RepositoryId = candidate.RepositoryId
            AND existing.CustomerPricingAssignmentId <> candidate.CustomerPricingAssignmentId
            AND candidate.EffectiveFromUtc < ISNULL(existing.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
            AND existing.EffectiveFromUtc < ISNULL(candidate.EffectiveToUtc, CONVERT(datetime2(7), ''9999-12-31T23:59:59.9999999''))
    )
    BEGIN
        THROW 57414, ''Customer pricing assignment effective windows cannot overlap for the same customer repository scope.'', 1;
    END;
END;');
"""
        )
        |> ignore

    /// Removes the pricing foundation schema in reverse dependency order.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS ops.{OperationsPricingSql.CustomerPricingAssignmentOverlapTriggerName};")
        |> ignore

        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS ops.{OperationsPricingSql.PricingRateOverlapTriggerName};")
        |> ignore

        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS ops.{OperationsPricingSql.BillableUsageKindMappingOverlapTriggerName};")
        |> ignore

        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS ops.{OperationsPricingSql.PricingPlanOverlapTriggerName};")
        |> ignore

        migrationBuilder.Sql(
            $"IF OBJECT_ID(N'{OperationsPricingSql.CustomerPricingAssignmentTable}', N'U') IS NOT NULL DROP TABLE {OperationsPricingSql.CustomerPricingAssignmentTable};"
        )
        |> ignore

        migrationBuilder.Sql($"IF OBJECT_ID(N'{OperationsPricingSql.PricingRateTable}', N'U') IS NOT NULL DROP TABLE {OperationsPricingSql.PricingRateTable};")
        |> ignore

        migrationBuilder.Sql(
            $"IF OBJECT_ID(N'{OperationsPricingSql.BillableUsageKindMappingTable}', N'U') IS NOT NULL DROP TABLE {OperationsPricingSql.BillableUsageKindMappingTable};"
        )
        |> ignore

        migrationBuilder.Sql($"IF OBJECT_ID(N'{OperationsPricingSql.PricingPlanTable}', N'U') IS NOT NULL DROP TABLE {OperationsPricingSql.PricingPlanTable};")
        |> ignore

    /// Captures the pricing foundation model that future migrations diff against.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        // Deliberately keep this migration target model frozen with literals so future runtime model edits
        // cannot change the historical reviewed migration point before a new migration updates it.
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
            .Property<byte array>("RawPayload")
            .HasColumnType("varbinary(max)")
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

        rawFact.Property<int>("ArchiveState").IsRequired()
        |> ignore

        rawFact
            .Property<string>("ArchiveBlobName")
            .HasMaxLength(512)
        |> ignore

        rawFact
            .Property<string>("ArchiveChecksumSha256Hex")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsUnicode(false)
        |> ignore

        rawFact
            .Property<System.Nullable<int64>>("ArchiveByteLength")
            .HasColumnType("bigint")
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("ArchiveVerifiedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("ArchivedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("RehydrationExpiresAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<string>("LastArchiveFailureReason")
            .HasMaxLength(400)
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("LastArchiveFailureAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<int>("ArchiveFailureCount")
            .IsRequired()
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("ArchiveRetiredAtUtc")
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
            .HasDatabaseName("IX_ops_RawUsageFact_RehydrationExpiresAtUtc")
            .HasFilter("[RehydrationExpiresAtUtc] IS NOT NULL")
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

        let pricingPlan = modelBuilder.Entity<PricingPlanEntity>()

        pricingPlan.ToTable("PricingPlan", "ops")
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
            .HasMaxLength(128)
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<string>("DisplayName")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
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

        mapping.ToTable("BillableUsageKindMapping", "ops")
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
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        mapping
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        mapping
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
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
            .HasDatabaseName("IX_ops_BillableUsageKindMapping_FactKindEffective")
        |> ignore

        let pricingRate = modelBuilder.Entity<PricingRateEntity>()

        pricingRate.ToTable("PricingRate", "ops")
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
            .HasMaxLength(3)
            .IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<string>("UnitName")
            .HasMaxLength(64)
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
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
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
            .HasDatabaseName("IX_ops_PricingRate_PlanUsageKindEffective")
        |> ignore

        pricingRate
            .HasOne(fun rate -> rate.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_PricingRate_PricingPlan")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let assignment = modelBuilder.Entity<CustomerPricingAssignmentEntity>()

        assignment.ToTable("CustomerPricingAssignment", "ops")
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
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
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
            .HasDatabaseName("IX_CustomerPricingAssignment_PricingPlanId")
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
            .HasDatabaseName("IX_ops_CustomerPricingAssignment_ScopeEffective")
        |> ignore

        assignment
            .HasOne(fun assignment -> assignment.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_CustomerPricingAssignment_PricingPlan")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore
