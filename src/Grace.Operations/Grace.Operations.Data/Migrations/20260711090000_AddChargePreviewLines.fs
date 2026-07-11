namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations

/// Adds deterministic provisional charge-preview lines without introducing billing-period lifecycle state.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260711090000_AddChargePreviewLines")>]
type AddChargePreviewLines() =
    inherit Migration()

    /// Applies the constrained preview table and complete-grain indexes.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
CREATE TABLE ops.ChargePreviewLine
(
    ChargePreviewLineId uniqueidentifier NOT NULL,
    CustomerId uniqueidentifier NOT NULL,
    OwnerId uniqueidentifier NOT NULL,
    OrganizationId uniqueidentifier NOT NULL,
    RepositoryId uniqueidentifier NOT NULL,
    PeriodFromUtc datetime2(7) NOT NULL,
    PeriodToUtc datetime2(7) NOT NULL,
    FactKind int NOT NULL,
    BillableUsageKindMappingId uniqueidentifier NOT NULL,
    BillableUsageKind int NOT NULL,
    CustomerPricingAssignmentId uniqueidentifier NOT NULL,
    PricingPlanId uniqueidentifier NOT NULL,
    PricingRateId uniqueidentifier NOT NULL,
    CurrencyCode varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    UnitName nvarchar(64) NOT NULL,
    UnitQuantity bigint NOT NULL,
    UnitPriceMicros bigint NOT NULL,
    EffectiveFromUtc datetime2(7) NOT NULL,
    EffectiveToUtc datetime2(7) NOT NULL,
    TotalQuantity bigint NOT NULL,
    ChargeMicros bigint NOT NULL,
    CONSTRAINT PK_ops_ChargePreviewLine PRIMARY KEY (ChargePreviewLineId),
    CONSTRAINT CK_ops_ChargePreviewLine_PeriodRange CHECK (PeriodFromUtc < PeriodToUtc),
    CONSTRAINT CK_ops_ChargePreviewLine_EffectiveRange CHECK (PeriodFromUtc <= EffectiveFromUtc AND EffectiveFromUtc < EffectiveToUtc AND EffectiveToUtc <= PeriodToUtc),
    CONSTRAINT CK_ops_ChargePreviewLine_UnitQuantity CHECK (UnitQuantity > 0),
    CONSTRAINT CK_ops_ChargePreviewLine_Amounts CHECK (UnitPriceMicros >= 0 AND TotalQuantity >= 0 AND ChargeMicros >= 0),
    CONSTRAINT CK_ops_ChargePreviewLine_Currency CHECK (LEN(CurrencyCode) = 3 AND CurrencyCode COLLATE Latin1_General_100_BIN2 = UPPER(CurrencyCode) COLLATE Latin1_General_100_BIN2 AND CurrencyCode COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Z]%')
);
CREATE INDEX IX_ops_ChargePreviewLine_Scope
    ON ops.ChargePreviewLine(CustomerId, OwnerId, OrganizationId, RepositoryId, PeriodFromUtc, PeriodToUtc);
CREATE UNIQUE INDEX UX_ops_ChargePreviewLine_CompleteGrain
    ON ops.ChargePreviewLine(CustomerId, OwnerId, OrganizationId, RepositoryId, PeriodFromUtc, PeriodToUtc,
        FactKind, BillableUsageKindMappingId, BillableUsageKind, CustomerPricingAssignmentId, PricingPlanId,
        PricingRateId, CurrencyCode, UnitName, UnitQuantity, UnitPriceMicros, EffectiveFromUtc, EffectiveToUtc);
"""
        )
        |> ignore

    /// Removes only the provisional preview persistence introduced by this migration.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.DropTable(OperationsChargePreviewSql.TableName, OperationsUsageSql.SchemaName)
        |> ignore

    /// Captures the complete Operations model represented by this migration.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        OperationsModel.configure modelBuilder
