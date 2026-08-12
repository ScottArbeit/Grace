namespace Grace.Operations.ProofHost

open Grace.Operations.Data
open Grace.Shared
open Grace.Types.Usage
open System
open System.Text.Json
open System.Threading

/// Runs the internal Operations append seam from an isolated process used only by AppHost integration proof.
module Program =

    /// Deserializes the complete supplied fact once for Operations-only proof commands.
    let private deserializeFact encodedFact =
        let factBytes = Convert.FromBase64String encodedFact
        let fact = JsonSerializer.Deserialize<UsageFact>(factBytes, Constants.JsonSerializerOptions)

        if isNull (box fact) then
            invalidArg (nameof encodedFact) "The usage fact payload is required."

        fact

    /// Appends one serialized fact through the production journal store without exposing a product route or test SQL shortcut.
    let private append connectionString encodedFact =
        let fact = deserializeFact encodedFact
        let store = SqlOperationsUsageJournalStore(connectionString)

        store
            .AppendAsync(fact, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    /// Evaluates the fact's exact scope through the production completeness store, preserving the root/Operations graph boundary.
    let private evaluateCompleteness connectionString encodedFact =
        let fact = deserializeFact encodedFact

        let scope =
            match UsageFactPersistencePlan.tryCreateCanonical fact with
            | Ok plan ->
                BillingCompletenessScope.tryCreate plan.RawFact.OwnerId plan.RawFact.OrganizationId plan.RawFact.RepositoryId plan.RawFact.ObservedAt
                |> Result.defaultWith (fun errors -> invalidOp (String.Join("; ", errors)))
            | Error errors -> invalidOp (String.Join("; ", errors))

        OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)
            .EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    /// Provides the test-only executable entry point used by the graph-independent AppHost tracer.
    [<EntryPoint>]
    let main arguments =
        if arguments.Length <> 3 then
            Console.Error.WriteLine("Usage: Grace.Operations.ProofHost <append|completeness> <operations-sql-connection-string> <base64-usage-fact-json>")
            64
        else
            try
                match arguments[0] with
                | "append" ->
                    let result = append arguments[1] arguments[2]

                    match result with
                    | Ok AppendedPending ->
                        Console.Out.WriteLine("appended-pending")
                        0
                    | Ok AlreadyPending ->
                        Console.Out.WriteLine("already-pending")
                        0
                    | Ok (AlreadyTerminal state) ->
                        Console.Out.WriteLine($"already-terminal:{state}")
                        0
                    | Error errors ->
                        Console.Error.WriteLine(String.Join("; ", errors))
                        1
                | "completeness" ->
                    match evaluateCompleteness arguments[1] arguments[2] with
                    | BlockedByUnresolvedUsageFactJournal ->
                        Console.Out.WriteLine("blocked-by-unresolved-usage-fact-journal")
                        0
                    | Complete ->
                        Console.Out.WriteLine("complete")
                        0
                    | state ->
                        Console.Out.WriteLine($"other-completeness:{state}")
                        1
                | command ->
                    Console.Error.WriteLine($"Unknown proof command '{command}'.")
                    64
            with
            | ex ->
                Console.Error.WriteLine(ex.ToString())
                1
