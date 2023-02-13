namespace Grace.Server.Tests

open NUnit.Framework

module Program =

    [<assembly:LevelOfParallelism(16)>]
    do()

    [<EntryPoint>]
    let main _ = 0
