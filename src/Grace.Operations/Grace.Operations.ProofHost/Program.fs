namespace Grace.Operations.ProofHost

open Grace.Operations.Data
open Grace.Shared
open Grace.Types.Usage
open System
open System.Text.Json
open System.Threading

/// Runs the internal Operations append seam from an isolated process used only by AppHost integration proof.
module Program =

    /// Appends one serialized fact through the production journal store without exposing a product route or test SQL shortcut.
    let private append connectionString encodedFact =
        let factBytes = Convert.FromBase64String encodedFact
        let fact = JsonSerializer.Deserialize<UsageFact>(factBytes, Constants.JsonSerializerOptions)

        if isNull (box fact) then
            invalidArg (nameof encodedFact) "The usage fact payload is required."

        let store = SqlOperationsUsageJournalStore(connectionString)

        store
            .AppendAsync(fact, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    /// Provides the test-only executable entry point used by the graph-independent AppHost tracer.
    [<EntryPoint>]
    let main arguments =
        if arguments.Length <> 2 then
            Console.Error.WriteLine("Usage: Grace.Operations.ProofHost <operations-sql-connection-string> <base64-usage-fact-json>")
            64
        else
            try
                let result = append arguments[0] arguments[1]

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
            with
            | ex ->
                Console.Error.WriteLine(ex.ToString())
                1
