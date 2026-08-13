namespace Grace.CLI.CacheEnrollment.ClaimHolder

open Grace.Cache
open System
open System.IO
open System.Threading

/// Hosts a normal-build test process that retains one production Cache enrollment claim until termination.
module Program =
    /// Runs the claim holder with its protected root and a signal file used by focused cross-process proof.
    [<EntryPoint>]
    let main arguments =
        match arguments with
        | [| root; signalPath |] ->
            match CacheIdentity.tryAcquireEnrollmentClaim root with
            | Error error ->
                Console.Error.WriteLine($"Could not acquire Cache enrollment claim: {error}")
                2
            | Ok claim ->
                try
                    File.WriteAllText(signalPath, "held")
                    use lifetime = new ManualResetEventSlim(false)
                    lifetime.Wait()
                    0
                finally
                    CacheIdentity.releaseEnrollmentClaim claim
        | _ ->
            Console.Error.WriteLine("Usage: Grace.CLI.CacheEnrollment.ClaimHolder <state-root> <held-signal-path>")
            64
