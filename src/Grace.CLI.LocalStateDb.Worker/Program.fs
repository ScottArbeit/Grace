namespace Grace.CLI.LocalStateDb.Worker

open System
open System.Threading.Tasks
open Grace.CLI
open Grace.Types.Types
open NodaTime

module Program =
    let private run (dbPath: string) (rootId: Guid) (rootHash: string) (iterations: int) =
        task {
            let baseTicks =
                SystemClock
                    .Instance
                    .GetCurrentInstant()
                    .ToUnixTimeTicks()

            let mutable index = 0

            while index < iterations do
                let ticks = baseTicks + int64 index

                let status =
                    { GraceStatus.Default with
                        RootDirectoryId = rootId
                        RootDirectorySha256Hash = rootHash
                        LastSuccessfulFileUpload = Instant.FromUnixTimeTicks(ticks)
                        LastSuccessfulDirectoryVersionUpload = Instant.FromUnixTimeTicks(ticks)
                    }

                do! LocalStateDb.applyStatusIncremental dbPath status Seq.empty Seq.empty
                index <- index + 1
        }

    [<EntryPoint>]
    let main argv =
        try
            if argv.Length < 4 then
                Console.Error.WriteLine("Usage: <dbPath> <rootId> <rootHash> <iterations>")
                2
            else
                let dbPath = argv[0]
                let rootId = Guid.Parse(argv[1])
                let rootHash = argv[2]
                let iterations = Int32.Parse(argv[3])

                run dbPath rootId rootHash iterations
                |> fun task -> task.GetAwaiter().GetResult()

                0
        with
        | ex ->
            Console.Error.WriteLine(ex.ToString())
            1
