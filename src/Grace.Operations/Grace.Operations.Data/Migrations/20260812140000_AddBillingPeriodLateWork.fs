namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Owns the frozen target declaration for the minimal Pending late-work handoff.
[<RequireQualifiedAccess>]
module BillingPeriodLateWorkFrozenTarget =

    /// Adds the complete late-work physical contract without using runtime model helpers.
    let apply (modelBuilder: ModelBuilder) =
        let lateWork = modelBuilder.Entity<BillingPeriodLateWorkEntity>()

        lateWork.ToTable(
            "BillingPeriodLateWork",
            "ops",
            fun (table: TableBuilder<BillingPeriodLateWorkEntity>) ->
                table.HasCheckConstraint(
                    "CK_ops_BillingPeriodLateWork_Identity",
                    "[BillingPeriodId] <> '00000000-0000-0000-0000-000000000000' AND [UsageFactId] <> '00000000-0000-0000-0000-000000000000'"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_BillingPeriodLateWork_State", "[State] = 0")
                |> ignore
        )
        |> ignore

        lateWork
            .HasKey([| "BillingPeriodId"; "UsageFactId" |])
            .HasName("PK_ops_BillingPeriodLateWork")
        |> ignore

        for name in [ "BillingPeriodId"; "UsageFactId" ] do
            lateWork
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .ValueGeneratedNever()
                .IsRequired()
            |> ignore

        lateWork
            .Property<int>("State")
            .HasColumnType("int")
            .HasDefaultValue(0)
            .IsRequired()
        |> ignore

        lateWork
            .Property<DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()", "DF_ops_BillingPeriodLateWork_CreatedAtUtc")
            .IsRequired()
        |> ignore

        lateWork
            .HasIndex([| "UsageFactId" |])
            .HasDatabaseName("IX_ops_BillingPeriodLateWork_UsageFact")
        |> ignore

        lateWork
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_BillingPeriodLateWork_BillingPeriod")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        lateWork
            .HasOne<RawUsageFactEntity>()
            .WithMany()
            .HasForeignKey("UsageFactId")
            .HasConstraintName("FK_ops_BillingPeriodLateWork_RawUsageFact")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

/// Adds the isolated post-close accepted-fact handoff after the direct-close schema.
[<DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260812140000_AddBillingPeriodLateWork")>]
type AddBillingPeriodLateWork() =
    inherit Migration()

    /// Creates the Pending handoff table and its period/raw-fact foreign-key contract.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
CREATE TABLE ops.BillingPeriodLateWork
(
    BillingPeriodId uniqueidentifier NOT NULL,
    UsageFactId uniqueidentifier NOT NULL,
    State int NOT NULL CONSTRAINT DF_ops_BillingPeriodLateWork_State DEFAULT (0),
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingPeriodLateWork_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ops_BillingPeriodLateWork PRIMARY KEY (BillingPeriodId, UsageFactId),
    CONSTRAINT FK_ops_BillingPeriodLateWork_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId),
    CONSTRAINT FK_ops_BillingPeriodLateWork_RawUsageFact FOREIGN KEY (UsageFactId) REFERENCES ops.RawUsageFact(UsageFactId),
    CONSTRAINT CK_ops_BillingPeriodLateWork_Identity CHECK ([BillingPeriodId] <> '00000000-0000-0000-0000-000000000000' AND [UsageFactId] <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT CK_ops_BillingPeriodLateWork_State CHECK ([State] = 0)
);
CREATE INDEX IX_ops_BillingPeriodLateWork_UsageFact ON ops.BillingPeriodLateWork(UsageFactId);
"""
        )
        |> ignore

    /// Removes only the late-work table introduced by this migration.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql("DROP TABLE ops.BillingPeriodLateWork;")
        |> ignore

    /// Captures the complete frozen target without reading mutable runtime configuration.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        BillingPeriodCloseFrozenTarget.applyPriorModel modelBuilder
        BillingPeriodCloseFrozenTarget.apply modelBuilder
        BillingPeriodLateWorkFrozenTarget.apply modelBuilder
