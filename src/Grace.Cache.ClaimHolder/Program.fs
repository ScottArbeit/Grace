namespace Grace.Cache.ClaimHolder

open System
open System.IO
open System.Threading
open Grace.Cache

/// Hosts a test-only process that directly owns one production Cache enrollment claim until it is terminated.
module Program =
    /// Runs the direct claim-holder process with a protected root and held-signal file path.
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
            Console.Error.WriteLine("Usage: Grace.Cache.ClaimHolder <state-root> <held-signal-path>")
            64
