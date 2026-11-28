namespace Grace.Operations.Tests

open Grace.Operations.Data
open NUnit.Framework
open System

/// Covers how Operations schema setup chooses its SQL connections.
[<TestFixture>]
type OperationsSchemaBootstrapTests() =
    /// Verifies default schema initialization uses the configured target database without requiring `master`.
    [<Test>]
    member _.DefaultSchemaBootstrapUsesTargetDatabaseConnectionOnly() =
        let plan =
            OperationsUsageSchemaBootstrapPlan.create
                "Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;"
                OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly

        let schemaBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(plan.SchemaConnectionString)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(plan.TargetDatabaseName, Is.EqualTo(Some "GraceOperations"))
                Assert.That(schemaBuilder.InitialCatalog, Is.EqualTo("GraceOperations"))
                Assert.That(plan.DatabaseCreationConnectionString, Is.EqualTo(None)))
        )

    /// Verifies database creation remains available only when an admin bootstrap mode is explicitly selected.
    [<Test>]
    member _.ExplicitSchemaBootstrapUsesMasterConnectionForDatabaseCreation() =
        let plan =
            OperationsUsageSchemaBootstrapPlan.create
                "Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;"
                OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing

        let creationBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(plan.DatabaseCreationConnectionString.Value)

        let schemaBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(plan.SchemaConnectionString)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(plan.TargetDatabaseName, Is.EqualTo(Some "GraceOperations"))
                Assert.That(creationBuilder.InitialCatalog, Is.EqualTo("master"))
                Assert.That(schemaBuilder.InitialCatalog, Is.EqualTo("GraceOperations")))
        )


    /// Verifies the AppHost builds the Server's Operations SQL data source with SqlClient comma-port syntax and IPv4 loopback.
    [<Test>]
    member _.AppHostFormatsLocalSqlDataSourceWithCommaPort() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(global.Program.BuildSqlTcpDataSource("localhost", 21433), Is.EqualTo("tcp:127.0.0.1,21433"))
                Assert.That(global.Program.BuildSqlTcpDataSource("tcp:localhost", "21433"), Is.EqualTo("tcp:127.0.0.1,21433")))
        )
