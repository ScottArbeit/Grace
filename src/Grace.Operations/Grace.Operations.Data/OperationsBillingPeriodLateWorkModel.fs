namespace Grace.Operations.Data

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Configures the minimal Pending post-close accepted-fact handoff.
[<RequireQualifiedAccess>]
module OperationsBillingPeriodLateWorkModel =

    /// Adds the period-and-fact handoff identity, SQL defaults, and restrictive foreign keys.
    let configure (modelBuilder: ModelBuilder) =
        let lateWork = modelBuilder.Entity<BillingPeriodLateWorkEntity>()

        lateWork.ToTable(
            "BillingPeriodLateWork",
            OperationsUsageSql.SchemaName,
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
            .HasDefaultValue(0, "DF_ops_BillingPeriodLateWork_State")
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
