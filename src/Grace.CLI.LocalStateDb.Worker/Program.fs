namespace Grace.CLI.LocalStateDb.Worker

open System
open System.Threading.Tasks
open Grace.CLI
open Grace.Types.Common
open NodaTime

module Program =
    let private run (dbPath: string) (rootId: Guid) (rootSha256Hash: string) (rootBlake3Hash: string) (iterations: int) =
        task {
            let baseTicks =
                SystemClock
                    .Instance
                    .GetCurrentInstant()
                    .ToUnixTimeTicks()

            let mutable index = 0

            while index < iterations do
                let ticks = baseTicks + int64 index

                let rootDirectory =
                    LocalDirectoryVersion.CreateWithHashes
                        rootId
                        Guid.Empty
                        Guid.Empty
                        Guid.Empty
                        "."
                        rootSha256Hash
                        rootBlake3Hash
                        (System.Collections.Generic.List<DirectoryVersionId>())
                        (System.Collections.Generic.List<LocalFileVersion>())
                        0L
                        DateTime.UtcNow

                let graceIndex = GraceIndex()
                graceIndex.TryAdd(rootId, rootDirectory) |> ignore

                let status =
                    { GraceStatus.Default with
                        Index = graceIndex
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootSha256Hash
                        LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(ticks)
                        LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(ticks)
                    }

                do! LocalStateDb.applyStatusIncremental dbPath status Seq.empty Seq.empty
                index <- index + 1
        }

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length < 5 then
                Console.Error.WriteLine("Usage: <dbPath> <rootId> <rootSha256Hash> <rootBlake3Hash> <iterations>")
                2
            else
                let dbPath = argv[0]
                let rootId = Guid.Parse(argv[1])
                let rootSha256Hash = argv[2]
                let rootBlake3Hash = argv[3]
                let iterations = Int32.Parse(argv[4])

                run dbPath rootId rootSha256Hash rootBlake3Hash iterations
                |> fun task -> task.GetAwaiter().GetResult()

                0
        with
        | ex ->
            Console.Error.WriteLine(ex.ToString())
            1
