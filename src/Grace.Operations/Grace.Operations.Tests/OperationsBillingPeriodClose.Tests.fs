namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.Data.SqlClient
open NUnit.Framework
open System
open System.Collections.Generic
open System.Data
open System.Threading
open System.Threading.Tasks
open System.Text.RegularExpressions

/// Captures a normalized physical schema slice without sharing declarations between the compared representations.
module private BillingCloseSchema =

    /// Retains whether a store type was explicitly configured or derived from the CLR/provider mapping.
    type StoreTypeProvenance =
        | Explicit of string
        | Inferred of string

    /// Holds every normalized set that belongs to the billing-close physical contract.
    type Representation =
        {
            Tables: Set<string>
            Columns: Set<string>
            Properties: Set<string>
            StoreTypeProvenance: Set<string>
            Keys: Set<string>
            Indexes: Set<string>
            ForeignKeys: Set<string>
            Checks: Set<string>
            Defaults: Set<string>
            Triggers: Set<string>
        }

    /// Names only the tables introduced by the billing-close migration with their SQL schema.
    let private tables =
        Set.ofList [ "ops.BillingPeriod"
                     "ops.Charge"
                     "ops.BillingPeriodCloseEvidence" ]

    /// Names tables that existed before the billing-close migration and are excluded from this leaf's live catalog slice.
    let private priorTables =
        "'__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine'"

    /// Forms the schema-qualified physical identity used by every catalog comparison facet.
    let private tableIdentity schema table = $"{schema}.{table}"

    /// Produces a stable SQL spelling without deriving aliases from configured provider types.
    let normalizeSql (value: string) =
        if String.IsNullOrWhiteSpace value then
            String.Empty
        else
            Regex.Replace(value.Trim().ToLowerInvariant(), "\\s+", " ")

    /// Produces a stable type spelling while preserving configured type names verbatim apart from case and whitespace.
    let normalizeStoreType (value: string) = Regex.Replace(normalizeSql value, "\\s+", "")

    /// Removes a single redundant outer SQL parenthesis layer when SQL Server adds it around a definition.
    let rec normalizeDefinition (value: string) =
        let normalized =
            value
            |> normalizeSql
            |> fun definition -> Regex.Replace(definition, "\\bdatepart\\(year,([^)]*)\\)", "year($1)")
            |> fun definition -> Regex.Replace(definition, "\\bdatepart\\(month,([^)]*)\\)", "month($1)")
            |> fun definition -> Regex.Replace(definition, "\\bnot ([^ ]+) like", "$1 not like")
            |> fun definition -> Regex.Replace(definition, "\\(\\[state\\] in \\(0, 1\\) and", "([state] = 1 or [state] = 0) and")
            |> fun definition -> Regex.Replace(definition, "\\[state\\] in \\(0, 1, 2\\)", "[state] = 2 or [state] = 1 or [state] = 0")
            |> fun definition -> Regex.Replace(definition, "\\((\\d+)\\)", "$1")
            |> fun definition -> Regex.Replace(definition, "\\((\\[[^]]+\\] is null and \\[[^]]+\\] is null)\\) or", "$1 or")
            |> fun definition -> Regex.Replace(definition, "\\s*>=\\s*", " >= ")
            |> fun definition -> Regex.Replace(definition, "\\s*<=\\s*", " <= ")
            |> fun definition -> Regex.Replace(definition, "(?<![<>=!])\\s*>\\s*(?![=])", " > ")
            |> fun definition -> Regex.Replace(definition, "(?<![<>=!])\\s*<\\s*(?![=])", " < ")
            |> fun definition -> Regex.Replace(definition, "(?<![<>=!])\\s*=\\s*(?![<>=])", " = ")
            |> fun definition -> Regex.Replace(definition, ",\\s*", ",")
            |> fun definition -> Regex.Replace(definition, "\\s+", " ").Trim()
            |> fun definition ->
                let opens = definition |> Seq.filter ((=) '(') |> Seq.length
                let closes = definition |> Seq.filter ((=) ')') |> Seq.length

                if closes > opens && definition.EndsWith(")") then
                    definition.TrimEnd(')')
                else
                    definition

        if
            normalized.Length > 1
            && normalized.StartsWith("(")
            && normalized.EndsWith(")")
        then
            let mutable depth = 0
            let mutable closesEarly = false

            for index in 0 .. normalized.Length - 2 do
                if normalized[index] = '(' then depth <- depth + 1
                elif normalized[index] = ')' then depth <- depth - 1

                if depth = 0 then closesEarly <- true

            if closesEarly then
                normalized
            else
                normalizeDefinition normalized[1 .. normalized.Length - 2]
        else
            normalized

    /// Renders an optional string as a stable set member segment.
    let private optional (value: string) = if String.IsNullOrWhiteSpace value then "-" else value

    /// Renders an optional integer as a stable set member segment.
    let private nullableInt (value: Nullable<int>) = if value.HasValue then string value.Value else "-"

    /// Renders an optional boolean as a stable set member segment.
    let private nullableBool (value: Nullable<bool>) = if value.HasValue then string value.Value else "-"

    /// Normalizes delete semantics to the SQL catalog's NO_ACTION term.
    let private normalizeDeleteBehavior behavior =
        match string behavior with
        | "Restrict"
        | "NoAction" -> "no_action"
        | value -> value.ToLowerInvariant()

    /// Reads configured type provenance without confusing a provider-derived mapping for an explicit declaration.
    let private storeTypeProvenance (property: IReadOnlyProperty) =
        match property.FindAnnotation(RelationalAnnotationNames.ColumnType) with
        | null -> Inferred(normalizeStoreType (property.GetColumnType()))
        | annotation ->
            match annotation.Value with
            | :? string as value when not (String.IsNullOrWhiteSpace value) -> Explicit(normalizeStoreType value)
            | _ -> Inferred(normalizeStoreType (property.GetColumnType()))

    /// Extracts exact model metadata for the three tables owned by this migration.
    let fromModel (model: IModel) =
        let entities =
            model.GetEntityTypes()
            |> Seq.filter (fun entity -> tables.Contains(tableIdentity (entity.GetSchema()) (entity.GetTableName())))
            |> Seq.toList

        let tableNames =
            entities
            |> Seq.map (fun entity -> tableIdentity (entity.GetSchema()) (entity.GetTableName()))
            |> Set.ofSeq

        let columns, properties, provenance =
            entities
            |> Seq.collect (fun entity ->
                let tableName = entity.GetTableName()
                let table = tableIdentity (entity.GetSchema()) tableName
                let storeObject = StoreObjectIdentifier.Table(tableName, entity.GetSchema())

                entity.GetProperties()
                |> Seq.map (fun property ->
                    let column = property.GetColumnName(&storeObject)
                    let inferredStoreType = normalizeStoreType (property.GetColumnType())

                    let columnEntry =
                        String.Join(
                            "|",
                            [|
                                table
                                column
                                inferredStoreType
                                string property.IsNullable
                                nullableInt (property.GetMaxLength())
                                nullableInt (property.GetPrecision())
                                nullableInt (property.GetScale())
                                nullableBool (property.IsUnicode())
                                nullableBool (property.IsFixedLength())
                                optional (property.GetCollation())
                            |]
                        )

                    let propertyEntry = $"{table}|{property.Name}|{column}|{property.ClrType.FullName}"

                    let provenanceEntry =
                        match storeTypeProvenance property with
                        | Explicit value -> $"{table}|{column}|explicit|{value}"
                        | Inferred value -> $"{table}|{column}|inferred|{value}"

                    columnEntry, propertyEntry, provenanceEntry))
            |> Seq.toList
            |> List.unzip3

        let keys =
            entities
            |> Seq.collect (fun entity ->
                let table = tableIdentity (entity.GetSchema()) (entity.GetTableName())

                entity.GetKeys()
                |> Seq.map (fun key ->
                    let kind =
                        if obj.ReferenceEquals(key, entity.FindPrimaryKey()) then
                            "primary"
                        else
                            "alternate"

                    let columns =
                        key.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> String.concat ","

                    $"{table}|{key.GetName()}|{kind}|{columns}"))
            |> Set.ofSeq

        let indexes =
            entities
            |> Seq.collect (fun entity ->
                let table = tableIdentity (entity.GetSchema()) (entity.GetTableName())

                entity.GetIndexes()
                |> Seq.map (fun index ->
                    let columns =
                        index.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> String.concat ","

                    $"{table}|{index.GetDatabaseName()}|{index.IsUnique}|{columns}|{optional (index.GetFilter())}"))
            |> Set.ofSeq

        let foreignKeys =
            entities
            |> Seq.collect (fun entity ->
                let table = tableIdentity (entity.GetSchema()) (entity.GetTableName())

                entity.GetForeignKeys()
                |> Seq.map (fun foreignKey ->
                    let dependentColumns =
                        foreignKey.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> String.concat ","

                    let principalColumns =
                        foreignKey.PrincipalKey.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> String.concat ","

                    let principal = $"{foreignKey.PrincipalEntityType.GetSchema()}.{foreignKey.PrincipalEntityType.GetTableName()}"
                    $"{table}|{foreignKey.GetConstraintName()}|{dependentColumns}|{principal}|{principalColumns}|{normalizeDeleteBehavior foreignKey.DeleteBehavior}"))
            |> Set.ofSeq

        let checks =
            entities
            |> Seq.collect (fun entity ->
                let table = tableIdentity (entity.GetSchema()) (entity.GetTableName())

                entity.GetCheckConstraints()
                |> Seq.map (fun check -> $"{table}|{check.Name}|{normalizeDefinition check.Sql}"))
            |> Set.ofSeq

        let defaults =
            entities
            |> Seq.collect (fun entity ->
                let tableName = entity.GetTableName()
                let table = tableIdentity (entity.GetSchema()) tableName

                entity.GetProperties()
                |> Seq.choose (fun property ->
                    let definition = property.GetDefaultValueSql()

                    if String.IsNullOrWhiteSpace definition then
                        None
                    else
                        let storeObject = StoreObjectIdentifier.Table(tableName, entity.GetSchema())
                        Some $"{table}|{property.GetColumnName(&storeObject)}|{property.GetDefaultConstraintName()}|{normalizeDefinition definition}"))
            |> Set.ofSeq

        let triggers =
            entities
            |> Seq.collect (fun entity ->
                let table = tableIdentity (entity.GetSchema()) (entity.GetTableName())

                entity.GetDeclaredTriggers()
                |> Seq.map (fun trigger -> $"{table}|{trigger.ModelName}"))
            |> Set.ofSeq

        {
            Tables = tableNames
            Columns = Set.ofList columns
            Properties = Set.ofList properties
            StoreTypeProvenance = Set.ofList provenance
            Keys = keys
            Indexes = indexes
            ForeignKeys = foreignKeys
            Checks = checks
            Defaults = defaults
            Triggers = triggers
        }

    /// Compares one exact set and reports both missing and unexpected members.
    let private compareSet name expected actual =
        let missing = Set.difference expected actual |> Set.toList
        let unexpected = Set.difference actual expected |> Set.toList

        let missingText = String.Join("; ", missing)
        let unexpectedText = String.Join("; ", unexpected)

        [
            if not missing.IsEmpty then yield $"{name} missing: {missingText}"
            if not unexpected.IsEmpty then yield $"{name} unexpected: {unexpectedText}"
        ]

    /// Compares all representation facets, including model-only CLR and explicit/inferred store metadata.
    let compare expected actual includeModelMetadata =
        [
            yield! compareSet "tables" expected.Tables actual.Tables
            yield! compareSet "columns" expected.Columns actual.Columns
            yield! compareSet "keys" expected.Keys actual.Keys
            yield! compareSet "indexes" expected.Indexes actual.Indexes
            yield! compareSet "foreign keys" expected.ForeignKeys actual.ForeignKeys
            yield! compareSet "checks" expected.Checks actual.Checks
            yield! compareSet "defaults" expected.Defaults actual.Defaults
            yield! compareSet "triggers" expected.Triggers actual.Triggers

            if includeModelMetadata then
                yield! compareSet "CLR properties" expected.Properties actual.Properties
                yield! compareSet "store-type provenance" expected.StoreTypeProvenance actual.StoreTypeProvenance
        ]

    /// Reads one exact normalized set from the disposable SQL Server catalog.
    let private catalogSetAsync connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let values = ResizeArray<string>()
            let mutable hasRow = true

            while hasRow do
                let! read = reader.ReadAsync CancellationToken.None
                hasRow <- read

                if read then values.Add(reader.GetString 0)

            return values |> Set.ofSeq
        }

    /// Extracts all physical facets from SQL Server without consulting an EF representation.
    let fromLiveSqlAsync connectionString =
        task {
            let! liveTables =
                catalogSetAsync
                    connectionString
                    $"SELECT CONCAT(s.name,'.',t.name) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='ops' AND t.name NOT IN ({priorTables});"

            let! columns =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(s.name COLLATE DATABASE_DEFAULT,'.',t.name COLLATE DATABASE_DEFAULT,'|',c.name COLLATE DATABASE_DEFAULT,'|',
    CASE ty.name COLLATE DATABASE_DEFAULT WHEN 'datetime2' THEN CONCAT('datetime2(',c.scale,')') WHEN 'nvarchar' THEN CONCAT('nvarchar(',c.max_length / 2,')') WHEN 'varchar' THEN CONCAT('varchar(',c.max_length,')') WHEN 'char' THEN CONCAT('char(',c.max_length,')') ELSE ty.name COLLATE DATABASE_DEFAULT END,
    '|',CASE c.is_nullable WHEN 1 THEN 'True' ELSE 'False' END,
    '|',CASE WHEN ty.name IN ('nvarchar','varchar','char') THEN CONVERT(varchar(10), CASE WHEN ty.name='nvarchar' THEN c.max_length / 2 ELSE c.max_length END) ELSE '-' END,
    '|-|-|',CASE WHEN ty.name IN ('varchar','char') THEN 'False' WHEN ty.name='nvarchar' THEN 'True' ELSE '-' END,
    '|',CASE WHEN ty.name='char' THEN 'True' ELSE '-' END,
    '|',CASE WHEN c.collation_name COLLATE DATABASE_DEFAULT='Latin1_General_100_BIN2' THEN c.collation_name COLLATE DATABASE_DEFAULT ELSE '-' END)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE s.name='ops' AND t.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine');
"""

            let! keys =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(s.name COLLATE DATABASE_DEFAULT,'.',t.name COLLATE DATABASE_DEFAULT,'|',kc.name COLLATE DATABASE_DEFAULT,'|',CASE kc.type COLLATE DATABASE_DEFAULT WHEN 'PK' THEN 'primary' ELSE 'alternate' END,'|',STRING_AGG(c.name COLLATE DATABASE_DEFAULT,',') WITHIN GROUP (ORDER BY ic.key_ordinal))
FROM sys.key_constraints kc
JOIN sys.tables t ON t.object_id=kc.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.index_columns ic ON ic.object_id=kc.parent_object_id AND ic.index_id=kc.unique_index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE s.name='ops' AND t.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine')
GROUP BY s.name,t.name,kc.name,kc.type;
"""

            let! indexes =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(s.name COLLATE DATABASE_DEFAULT,'.',t.name COLLATE DATABASE_DEFAULT,'|',i.name COLLATE DATABASE_DEFAULT,'|',CASE i.is_unique WHEN 1 THEN 'True' ELSE 'False' END,'|',STRING_AGG(c.name COLLATE DATABASE_DEFAULT,',') WITHIN GROUP (ORDER BY ic.key_ordinal),'|',COALESCE(i.filter_definition COLLATE DATABASE_DEFAULT,'-'))
FROM sys.indexes i
JOIN sys.tables t ON t.object_id=i.object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal > 0
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE s.name='ops' AND t.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine') AND i.is_primary_key=0 AND i.is_unique_constraint=0
GROUP BY s.name,t.name,i.name,i.is_unique,i.filter_definition;
"""

            let! foreignKeys =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(childSchema.name COLLATE DATABASE_DEFAULT,'.',child.name COLLATE DATABASE_DEFAULT,'|',fk.name COLLATE DATABASE_DEFAULT,'|',STRING_AGG(childColumn.name COLLATE DATABASE_DEFAULT,',') WITHIN GROUP (ORDER BY fkc.constraint_column_id),'|',principalSchema.name COLLATE DATABASE_DEFAULT,'.',parent.name COLLATE DATABASE_DEFAULT,'|',STRING_AGG(parentColumn.name COLLATE DATABASE_DEFAULT,',') WITHIN GROUP (ORDER BY fkc.constraint_column_id),'|',LOWER(fk.delete_referential_action_desc) COLLATE DATABASE_DEFAULT)
FROM sys.foreign_keys fk
JOIN sys.tables child ON child.object_id=fk.parent_object_id
JOIN sys.schemas childSchema ON childSchema.schema_id=child.schema_id
JOIN sys.tables parent ON parent.object_id=fk.referenced_object_id
JOIN sys.schemas principalSchema ON principalSchema.schema_id=parent.schema_id
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.columns childColumn ON childColumn.object_id=child.object_id AND childColumn.column_id=fkc.parent_column_id
JOIN sys.columns parentColumn ON parentColumn.object_id=parent.object_id AND parentColumn.column_id=fkc.referenced_column_id
WHERE childSchema.name='ops' AND child.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine')
GROUP BY childSchema.name,child.name,fk.name,principalSchema.name,parent.name,fk.delete_referential_action_desc;
"""

            let! checks =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(s.name COLLATE DATABASE_DEFAULT,'.',t.name COLLATE DATABASE_DEFAULT,'|',cc.name COLLATE DATABASE_DEFAULT,'|',cc.definition COLLATE DATABASE_DEFAULT)
FROM sys.check_constraints cc
JOIN sys.tables t ON t.object_id=cc.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='ops' AND t.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine');
"""

            let! defaults =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(s.name COLLATE DATABASE_DEFAULT,'.',t.name COLLATE DATABASE_DEFAULT,'|',c.name COLLATE DATABASE_DEFAULT,'|',dc.name COLLATE DATABASE_DEFAULT,'|',dc.definition COLLATE DATABASE_DEFAULT)
FROM sys.default_constraints dc
JOIN sys.tables t ON t.object_id=dc.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=dc.parent_column_id
WHERE s.name='ops' AND t.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine');
"""

            let! triggers =
                catalogSetAsync
                    connectionString
                    """
SELECT CONCAT(s.name COLLATE DATABASE_DEFAULT,'.',t.name COLLATE DATABASE_DEFAULT,'|',tr.name COLLATE DATABASE_DEFAULT)
FROM sys.triggers tr
JOIN sys.tables t ON t.object_id=tr.parent_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='ops' AND t.name NOT IN ('__EFMigrationsHistory','RawUsageFact','UsageFactRejection','UsageFactJournal','UsageAggregateMinute','PricingPlan','BillableUsageKindMapping','PricingRate','PricingAssignment','ChargePreviewLine');
"""

            return
                {
                    Tables = liveTables
                    Columns = columns
                    Properties = Set.empty
                    StoreTypeProvenance = Set.empty
                    Keys = keys
                    Indexes = indexes
                    ForeignKeys = foreignKeys
                    Checks =
                        checks
                        |> Set.map (fun value -> let fields = value.Split('|', 3) in $"{fields[0]}|{fields[1]}|{normalizeDefinition fields[2]}")
                    Defaults =
                        defaults
                        |> Set.map (fun value -> let fields = value.Split('|', 4) in $"{fields[0]}|{fields[1]}|{fields[2]}|{normalizeDefinition fields[3]}")
                    Triggers = triggers
                }
        }

    /// Replaces exactly one member in a set so false-pass probes cannot mutate production source files.
    let replace original replacement members =
        members
        |> Set.remove original
        |> Set.add replacement

/// Establishes the billing-close physical contract tests without adding close behavior.
[<TestFixture>]
[<NonParallelizable>]
type OperationsBillingPeriodCloseTests() =

    /// Names the SQL Server connection used only for disposable physical-schema tests.
    [<Literal>]
    let sqlConnectionStringEnvironmentVariable = "GRACE_OPERATIONS_SQL_TEST_CONNECTION_STRING"

    /// Creates a database name that cannot share rows with another test run.
    let databaseName () = $"GraceOperationsBillingCloseSchema_{Guid.NewGuid():N}"

    /// Explicitly skips only live SQL proofs when no disposable SQL Server was configured for this process.
    let requireSqlConnectionString () =
        let value = Environment.GetEnvironmentVariable sqlConnectionStringEnvironmentVariable

        if String.IsNullOrWhiteSpace value then
            Assert.Ignore(
                $"{sqlConnectionStringEnvironmentVariable} is unavailable; live SQL proof is skipped for this run and remains required in the isolated Docker gate."
            )

        value

    /// Creates one disposable database by running the production Operations migration initializer.
    let createDatabaseAsync () =
        task {
            let builder = SqlConnectionStringBuilder(requireSqlConnectionString ())
            builder.InitialCatalog <- databaseName ()
            let schema = OperationsUsageSchema(builder.ConnectionString, OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing)
            do! schema.EnsureCreatedAsync CancellationToken.None
            return builder.ConnectionString
        }

    /// Removes a disposable database whether the contained proof passes or fails.
    let dropDatabaseAsync connectionString =
        task {
            let builder = SqlConnectionStringBuilder(connectionString)
            let database = builder.InitialCatalog
            builder.InitialCatalog <- "master"
            use connection = new SqlConnection(builder.ConnectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];"
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Runs a physical proof with deterministic disposal of its isolated SQL database.
    let withDatabaseAsync operation =
        task {
            let! connectionString = createDatabaseAsync ()

            try
                let! result = operation connectionString
                do! dropDatabaseAsync connectionString
                return result
            with
            | ex ->
                do! dropDatabaseAsync connectionString
                return raise ex
        }

    /// Fails with every missing and unexpected normalized schema member rather than a selected membership assertion.
    let assertNoDrift (label: string) (drift: string list) =
        let details = String.Join(Environment.NewLine, drift)
        Assert.That(drift, Is.Empty, $"{label} drift: {details}")

    /// Executes a physical statement and returns its scalar result when the SQL proof needs one.
    let executeAsync connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Captures every durable value touched by this leaf before and after one rejected physical statement.
    let durableSnapshotAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT CONCAT('BillingPeriod|',(SELECT * FROM ops.BillingPeriod ORDER BY BillingPeriodId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('ChargePreviewLine|',(SELECT * FROM ops.ChargePreviewLine ORDER BY ChargePreviewLineId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('Charge|',(SELECT * FROM ops.Charge ORDER BY ChargeId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('BillingPeriodCloseEvidence|',(SELECT * FROM ops.BillingPeriodCloseEvidence ORDER BY BillingPeriodId FOR JSON PATH, INCLUDE_NULL_VALUES))
ORDER BY 1;
"""

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let values = ResizeArray<string>()
            let mutable hasRow = true

            while hasRow do
                let! read = reader.ReadAsync CancellationToken.None
                hasRow <- read
                if read then values.Add(reader.GetString 0)

            return values |> Seq.toList
        }

    /// Requires one invalid physical statement to fail and to preserve the complete durable projection.
    let rejectAndPreserveAsync connectionString label sql =
        task {
            let! before = durableSnapshotAsync connectionString
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql

            Assert.ThrowsAsync<SqlException>(Func<Task>(fun () -> command.ExecuteNonQueryAsync(CancellationToken.None) :> Task))
            |> ignore

            let! after = durableSnapshotAsync connectionString
            Assert.That<string list>(after, Is.EqualTo<string list>(before), $"{label} changed durable state after SQL rejected it.")
        }

    /// Inserts one valid period, preview line, charge, and evidence without invoking any future close behavior.
    let seedValidRowsAsync connectionString =
        executeAsync
            connectionString
            """
DECLARE @PeriodId uniqueidentifier='11111111-1111-1111-1111-111111111111', @LineId uniqueidentifier='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc)
VALUES (@PeriodId,NEWID(),NEWID(),NEWID(),'2026-06-01','2026-07-01',0,NULL,NULL);
INSERT ops.ChargePreviewLine (ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros)
VALUES (@LineId,NEWID(),NEWID(),NEWID(),'2026-06-01','2026-07-01',1,NEWID(),1,NEWID(),NEWID(),NEWID(),'USD','unit',1,1,'2026-06-01','2026-07-01',1,1);
INSERT ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) VALUES (NEWID(),@PeriodId,@LineId,'USD',1);
INSERT ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance)
VALUES (@PeriodId,REPLICATE('A',64),REPLICATE('B',64),SYSUTCDATETIME(),'schema-fixture');
DECLARE @AmountPeriodId uniqueidentifier='22222222-2222-2222-2222-222222222222', @AmountLineId uniqueidentifier='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State) VALUES (@AmountPeriodId,NEWID(),NEWID(),NEWID(),'2026-07-01','2026-08-01',0);
INSERT ops.ChargePreviewLine (ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES (@AmountLineId,NEWID(),NEWID(),NEWID(),'2026-07-01','2026-08-01',1,NEWID(),1,NEWID(),NEWID(),NEWID(),'USD','unit',1,1,'2026-07-01','2026-08-01',1,1);
DECLARE @CurrencyPeriodId uniqueidentifier='33333333-3333-3333-3333-333333333333', @CurrencyLineId uniqueidentifier='cccccccc-cccc-cccc-cccc-cccccccccccc';
INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State) VALUES (@CurrencyPeriodId,NEWID(),NEWID(),NEWID(),'2026-08-01','2026-09-01',0);
INSERT ops.ChargePreviewLine (ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES (@CurrencyLineId,NEWID(),NEWID(),NEWID(),'2026-08-01','2026-09-01',1,NEWID(),1,NEWID(),NEWID(),NEWID(),'USD','unit',1,1,'2026-08-01','2026-09-01',1,1);
INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State) VALUES ('44444444-4444-4444-4444-444444444444',NEWID(),NEWID(),NEWID(),'2026-09-01','2026-10-01',0),('55555555-5555-5555-5555-555555555555',NEWID(),NEWID(),NEWID(),'2026-10-01','2026-11-01',0);
"""

    /// Proves configured provider types retain their explicit provenance and cannot silently become CLR aliases.
    [<Test>]
    member _.RedProbeExplicitStoreTypeDriftIsNotIgnored() =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingCloseModel;Integrated Security=true;"

        let runtime =
            context.GetService<IDesignTimeModel>().Model
            |> BillingCloseSchema.fromModel

        let original = "ops.Charge|CurrencyCode|explicit|varchar(3)"

        let mutated =
            { runtime with
                StoreTypeProvenance = BillingCloseSchema.replace original "ops.Charge|CurrencyCode|explicit|System.Int32" runtime.StoreTypeProvenance
            }

        let drift = BillingCloseSchema.compare runtime mutated true
        Assert.That(drift, Is.Not.Empty)

    /// Proves complete-set comparison reports a missing live column instead of accepting selected membership.
    [<Test>]
    member _.RedProbeIncompleteLiveSetIsNotIgnored() =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingCloseModel;Integrated Security=true;"

        let runtime =
            context.GetService<IDesignTimeModel>().Model
            |> BillingCloseSchema.fromModel

        let incomplete =
            { runtime with
                Columns =
                    runtime.Columns
                    |> Set.remove "ops.Charge|ChargeMicros|bigint|False|-|-|-|-|-|-"
            }

        let drift = BillingCloseSchema.compare runtime incomplete false
        Assert.That(drift, Is.Not.Empty)

    /// Compares runtime, independently frozen migration target, independently declared snapshot, and migrated SQL catalog exactly.
    [<Test>]
    member _.RuntimeTargetSnapshotAndLiveSqlPhysicalSchemasAgreeExactly() =
        withDatabaseAsync (fun connectionString ->
            task {
                use context =
                    OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingCloseModel;Integrated Security=true;"

                let runtime =
                    context.GetService<IDesignTimeModel>().Model
                    |> BillingCloseSchema.fromModel

                let target =
                    AddBillingPeriodClose().TargetModel
                    |> BillingCloseSchema.fromModel

                let snapshot =
                    OperationsDbContextModelSnapshot().Model
                    |> BillingCloseSchema.fromModel

                let! live = BillingCloseSchema.fromLiveSqlAsync connectionString
                assertNoDrift "runtime-target" (BillingCloseSchema.compare runtime target true)
                assertNoDrift "runtime-snapshot" (BillingCloseSchema.compare runtime snapshot true)
                assertNoDrift "runtime-live" (BillingCloseSchema.compare runtime live false)

                do! executeAsync connectionString "CREATE TABLE ops.UnexpectedPhysicalObject (UnexpectedId uniqueidentifier NOT NULL);"
                let! liveWithUnexpectedObject = BillingCloseSchema.fromLiveSqlAsync connectionString

                Assert.That(
                    BillingCloseSchema.compare runtime liveWithUnexpectedObject false,
                    Is.Not.Empty,
                    "The live catalog extractor must report an unexpected physical table instead of prefiltering it away."
                )
            })

    /// Verifies five independent altered fixture representations all produce exact-set drift reports.
    [<Test>]
    member _.FalsePassMutationsCannotCompareAsParity() =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingCloseModel;Integrated Security=true;"

        let runtime =
            context.GetService<IDesignTimeModel>().Model
            |> BillingCloseSchema.fromModel

        let mutations =
            [
                { runtime with
                    StoreTypeProvenance =
                        BillingCloseSchema.replace
                            "ops.Charge|CurrencyCode|explicit|varchar(3)"
                            "ops.Charge|CurrencyCode|explicit|System.Int32"
                            runtime.StoreTypeProvenance
                }
                { runtime with
                    Checks =
                        BillingCloseSchema.replace
                            "ops.Charge|CK_ops_Charge_Amount|[chargemicros] >= 0"
                            "ops.Charge|CK_ops_Charge_Amount|[chargemicros] > 0"
                            runtime.Checks
                }
                { runtime with
                    Indexes =
                        BillingCloseSchema.replace
                            "ops.Charge|UX_ops_Charge_InitialPosting|True|BillingPeriodId,ChargePreviewLineId|-"
                            "ops.Charge|UX_ops_Charge_InitialPosting|True|ChargePreviewLineId,BillingPeriodId|-"
                            runtime.Indexes
                }
                { runtime with
                    ForeignKeys =
                        BillingCloseSchema.replace
                            "ops.Charge|FK_ops_Charge_BillingPeriod|BillingPeriodId|ops.BillingPeriod|BillingPeriodId|no_action"
                            "ops.Charge|FK_ops_Charge_BillingPeriod|BillingPeriodId|ops.BillingPeriod|BillingPeriodId|cascade"
                            runtime.ForeignKeys
                }
                { runtime with
                    Triggers = BillingCloseSchema.replace "ops.Charge|TR_ops_Charge_Immutable" "ops.Charge|TR_ops_Charge_Immutable_Renamed" runtime.Triggers
                }
                { runtime with Tables = BillingCloseSchema.replace "ops.Charge" "dbo.Charge" runtime.Tables }
                { runtime with
                    Properties =
                        BillingCloseSchema.replace
                            "ops.Charge|CurrencyCode|CurrencyCode|System.String"
                            "ops.Charge|Currency|CurrencyCode|System.String"
                            runtime.Properties
                }
                { runtime with
                    Tables =
                        runtime.Tables
                        |> Set.add "ops.UnexpectedPhysicalObject"
                }
            ]

        mutations
        |> List.iter (fun mutated -> Assert.That(BillingCloseSchema.compare runtime mutated true, Is.Not.Empty))

    /// Guards snapshot independence from the migration target helper for the close fragment.
    [<Test>]
    member _.SnapshotCloseFragmentDoesNotCallMigrationFrozenTargetHelper() =
        let sourcePath =
            IO.Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                "..",
                "Grace.Operations.Data",
                "Migrations",
                "OperationsDbContextModelSnapshot.fs"
            )
            |> IO.Path.GetFullPath

        let source = IO.File.ReadAllText sourcePath
        Assert.That(source, Does.Not.Contain("BillingPeriodCloseFrozenTarget.apply"))

    /// Exercises every #916 check, identity, foreign-key, and immutable-trigger rejection against one migrated SQL database.
    [<Test>]
    member _.LivePhysicalRejectionMatrixRejectsFifteenNamedInvalidStatementsAndAllowsValidRows() =
        withDatabaseAsync (fun connectionString ->
            task {
                do! seedValidRowsAsync connectionString

                let cases =
                    [
                        "period state",
                        "INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State) VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-06-01','2026-07-01',3);"
                        "period month",
                        "INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State) VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-06-02','2026-07-02',0);"
                        "period diagnostic",
                        "INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc) VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-06-01','2026-07-01',2,'retry',SYSUTCDATETIME());"
                        "charge amount",
                        "INSERT ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) VALUES (NEWID(),'22222222-2222-2222-2222-222222222222','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','USD',-1);"
                        "charge currency",
                        "INSERT ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) VALUES (NEWID(),'33333333-3333-3333-3333-333333333333','cccccccc-cccc-cccc-cccc-cccccccccccc','usd',1);"
                        "evidence digest",
                        "INSERT ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance) VALUES ('44444444-4444-4444-4444-444444444444','bad',REPLICATE('A',64),SYSUTCDATETIME(),'x');"
                        "evidence provenance",
                        "INSERT ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance) VALUES ('55555555-5555-5555-5555-555555555555',REPLICATE('A',64),REPLICATE('B',64),SYSUTCDATETIME(),' ');"
                        "duplicate posting",
                        "INSERT ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) SELECT NEWID(),BillingPeriodId,ChargePreviewLineId,'USD',1 FROM ops.Charge;"
                        "charge period foreign key",
                        "INSERT ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) SELECT NEWID(),NEWID(),ChargePreviewLineId,'USD',1 FROM ops.Charge;"
                        "charge preview foreign key",
                        "INSERT ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) SELECT NEWID(),BillingPeriodId,NEWID(),'USD',1 FROM ops.Charge;"
                        "evidence period foreign key",
                        "INSERT ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance) VALUES (NEWID(),REPLICATE('A',64),REPLICATE('B',64),SYSUTCDATETIME(),'x');"
                        "charge update", "UPDATE ops.Charge SET ChargeMicros=2;"
                        "charge delete", "DELETE FROM ops.Charge;"
                        "evidence update", "UPDATE ops.BillingPeriodCloseEvidence SET ScheduledOperationProvenance='tampered';"
                        "evidence delete", "DELETE FROM ops.BillingPeriodCloseEvidence;"
                    ]

                Assert.That(cases.Length, Is.EqualTo(15), "The physical rejection matrix must retain every named #916 case.")

                for label, sql in cases do
                    do! rejectAndPreserveAsync connectionString label sql

                do!
                    executeAsync
                        connectionString
                        "INSERT ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State) VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-07-01','2026-08-01',0);"

                Assert.That(true, Is.True, "Valid physical insert succeeded after every rejected statement.")
            })
