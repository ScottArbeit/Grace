namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.CommandOutputContract
open NUnit.Framework

/// Groups output-contract coverage for static Cache commands.
[<Parallelizable(ParallelScope.All)>]
module CacheCliTests =

    /// Verifies that cache enrollment and redacted local status are registered with standard CLI output handling.
    [<Test>]
    let ``cache commands are registered in the output contract`` () =
        let commandIds =
            entries
            |> List.map (fun entry -> entry.Identity.CommandId)

        commandIds |> should contain "cache.enroll"
        commandIds |> should contain "cache.status"
