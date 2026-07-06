namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure

/// Captures the current Operations EF model so future migrations can detect reviewed schema drift.
[<DbContextAttribute(typeof<OperationsDbContext>)>]
type OperationsDbContextModelSnapshot() =
    inherit ModelSnapshot()

    /// Rebuilds the Operations model represented by the latest migration.
    override _.BuildModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        OperationsModel.configure modelBuilder
