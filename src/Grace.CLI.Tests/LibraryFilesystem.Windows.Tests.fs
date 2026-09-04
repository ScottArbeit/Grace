namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.IO
open System.Security.Cryptography

/// Verifies the narrow Windows stable-read and same-volume atomic publication adapter.
[<NonParallelizable>]
module LibraryFilesystemWindowsTests =

    /// Runs one filesystem test in a disposable Windows directory.
    let private withRoot action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-library-filesystem-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Computes the accepted lower-case SHA-256 hash for a focused test payload.
    let private sha256 (bytes: byte array) =
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun hash -> hash.ToLowerInvariant()

    /// Verifies a source mutation at the read boundary invalidates publication input.
    [<Test>]
    let ``stable read rejects a file changed during the read`` () =
        if OperatingSystem.IsWindows() then
            withRoot (fun root ->
                let path = Path.Combine(root, "file.txt")
                File.WriteAllText(path, "before")

                (fun () ->
                    LibraryFilesystem.stableReadWith (fun () -> File.WriteAllText(path, "after-with-different-size")) path
                    |> ignore)
                |> should throw typeof<InvalidOperationException>)

    /// Verifies same-directory publication leaves the exact accepted bytes and no staging file.
    [<Test>]
    let ``atomic publication replaces the target with exact bytes`` () =
        if OperatingSystem.IsWindows() then
            withRoot (fun root ->
                let target = Path.Combine(root, "nested", "file.bin")
                let bytes = [| 0uy; 1uy; 2uy; 255uy |]

                LibraryFilesystem.publishAtomic target (ContentAddress.computeBlake3Hex bytes) (sha256 bytes) (int64 bytes.Length) bytes

                File.ReadAllBytes(target) |> should equal bytes

                Directory.GetFiles(Path.GetDirectoryName(target), ".grace-library-*.tmp")
                |> should be Empty)
