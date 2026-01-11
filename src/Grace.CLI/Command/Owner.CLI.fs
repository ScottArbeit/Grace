namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open NodaTime
open System
open System.Collections.Generic
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.CommandLine
open Spectre.Console
open Spectre.Console.Json
open Grace.Shared.Utilities
open Grace.CLI
open Grace.CLI.Services

module Owner =

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                [||],
                Required = false,
                Description = "The Id of the owner <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The name of the owner. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let ownerNameRequired =
            new Option<String>(OptionName.OwnerName, Required = true, Description = "The name of the owner.", Arity = ArgumentArity.ExactlyOne)

        let ownerTypeRequired =
            (new Option<String>(OptionName.OwnerType, Required = true, Description = "The type of owner. [default: Public]", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(Utilities.listCases<OwnerType> ())

        let searchVisibilityRequired =
            (new Option<String>(
                OptionName.SearchVisibility,
                Required = true,
                Description = "Enables or disables the owner appearing in searches. [default: true]",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(Utilities.listCases<SearchVisibility> ())

        let descriptionRequired =
            new Option<String>(OptionName.Description, Required = true, Description = "Description of the owner.", Arity = ArgumentArity.ExactlyOne)

        let newName =
            new Option<String>(OptionName.NewName, Required = true, Description = "The new name of the organization.", Arity = ArgumentArity.ExactlyOne)

        let force = new Option<bool>(OptionName.Force, Required = false, Description = "Delete even if there is data under this owner. [default: false]")

        let includeDeleted =
            new Option<bool>(OptionName.IncludeDeleted, [| "-d" |], Required = false, Description = "Include deleted owners in the result. [default: false]")

        let deleteReason =
            new Option<String>(OptionName.DeleteReason, Required = true, Description = "The reason for deleting the owner.", Arity = ArgumentArity.ExactlyOne)

        let doNotSwitch =
            new Option<bool>(
                OptionName.DoNotSwitch,
                Required = false,
                Description = "Do not switch your current owner to the new owner after it is created. By default, the new owner becomes the current owner.",
                Arity = ArgumentArity.ZeroOrOne
            )

    let ownerCommonValidations =
        CommonValidations
        >=> ``Either OwnerId or OwnerName must be provided``

    // Create subcommand.
    type Create() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    // In a Create() command, if --owner-id is implicit, that's actually the old OwnerId taken from graceconfig.json,
                    //   and we need to set OwnerId to a new Guid.
                    let mutable graceIds = parseResult |> getNormalizedIdsAndNames

                    if parseResult.GetResult(Options.ownerId).Implicit then
                        let ownerId = Guid.NewGuid()
                        graceIds <- { graceIds with OwnerId = ownerId; OwnerIdString = $"{ownerId}" }

                    let validateIncomingParameters =
                        parseResult
                        |> ownerCommonValidations
                        >>= (``Option must be present`` OptionName.OwnerName OwnerNameIsRequired)

                    match validateIncomingParameters with
                    | Ok _ ->
                        let ownerId =
                            if parseResult.GetResult(Options.ownerId).Implicit then
                                Guid.NewGuid().ToString()
                            else
                                graceIds.OwnerIdString

                        let parameters =
                            Parameters.Owner.CreateOwnerParameters(OwnerId = ownerId, OwnerName = graceIds.OwnerName, CorrelationId = graceIds.CorrelationId)

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.Create(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            match result with
                            | Ok returnValue ->
                                // Update the Grace configuration file with the newly-created owner.
                                if not <| parseResult.GetValue(Options.doNotSwitch) then
                                    let newConfig = Current()
                                    newConfig.OwnerId <- Guid.Parse($"{returnValue.Properties[nameof OwnerId]}")
                                    newConfig.OwnerName <- $"{returnValue.Properties[nameof OwnerName]}"
                                    logToAnsiConsole Colors.Verbose $"newConfig: {serialize newConfig}."
                                    updateConfiguration newConfig

                                return result |> renderOutput parseResult
                            | Error _ -> return result |> renderOutput parseResult
                        else
                            let! result = Owner.Create(parameters)

                            match result with
                            | Ok returnValue ->
                                // Update the Grace configuration file with the newly-created owner.
                                if not <| parseResult.GetValue(Options.doNotSwitch) then
                                    let newConfig = Current()
                                    newConfig.OwnerId <- Guid.Parse($"{returnValue.Properties[nameof OwnerId]}")
                                    newConfig.OwnerName <- $"{returnValue.Properties[nameof OwnerName]}"
                                    logToAnsiConsole Colors.Verbose $"newConfig: {serialize newConfig}."
                                    updateConfiguration newConfig

                                return result |> renderOutput parseResult
                            | Error _ -> return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // Get subcommand
    type Get() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.GetOwnerParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                IncludeDeleted = parseResult.GetValue(Options.includeDeleted),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.Get(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            match result with
                            | Ok graceReturnValue ->
                                let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                                AnsiConsole.Write(jsonText)
                                AnsiConsole.WriteLine()
                                return Ok graceReturnValue |> renderOutput parseResult
                            | Error graceError -> return Error graceError |> renderOutput parseResult
                        else
                            let! result = Owner.Get(parameters)

                            match result with
                            | Ok graceReturnValue ->
                                let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                                AnsiConsole.Write(jsonText)
                                AnsiConsole.WriteLine()
                                return Ok graceReturnValue |> renderOutput parseResult
                            | Error graceError -> return Error graceError |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetName subcommand
    type SetName() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.SetOwnerNameParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                NewName = parseResult.GetValue(Options.newName),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.SetName(parameters)

                                            match result with
                                            | Ok returnValue ->
                                                // Update the Grace configuration file with the new Owner name.
                                                let newConfig = Current()
                                                newConfig.OwnerName <- parseResult.GetValue(Options.newName)
                                                updateConfiguration newConfig
                                            | Error _ -> ()

                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Owner.SetName(parameters)

                            match result with
                            | Ok graceReturnValue ->
                                // Update the Grace configuration file with the new Owner name.
                                let newConfig = Current()
                                newConfig.OwnerName <- parseResult.GetValue(Options.newName)
                                updateConfiguration newConfig
                            | Error _ -> ()

                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetType subcommand
    type SetType() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.SetOwnerTypeParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OwnerType = parseResult.GetValue(Options.ownerTypeRequired),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.SetType(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Owner.SetType(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetSearchVisibility subcommand
    type SetSearchVisibility() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.SetOwnerSearchVisibilityParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                SearchVisibility = parseResult.GetValue(Options.searchVisibilityRequired),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.SetSearchVisibility(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Owner.SetSearchVisibility(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // SetDescription subcommand
    type SetDescription() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.SetOwnerDescriptionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                Description = parseResult.GetValue(Options.descriptionRequired),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.SetDescription(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Owner.SetDescription(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // Delete subcommand
    type Delete() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.DeleteOwnerParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                Force = parseResult.GetValue(Options.force),
                                DeleteReason = parseResult.GetValue(Options.deleteReason),
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.Delete(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Owner.Delete(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // Undelete subcommand
    type Undelete() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> ownerCommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Owner.UndeleteOwnerParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Owner.Undelete(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            return result |> renderOutput parseResult
                        else
                            let! result = Owner.Undelete(parameters)
                            return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with
                | ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId

        // Create main command and aliases, if any.`
        let ownerCommand = new Command("owner", Description = "Create, change, or delete owner-level information.")

        // Add subcommands.
        let ownerCreateCommand =
            new Command("create", Description = "Create a new owner.")
            |> addOption Options.ownerNameRequired
            |> addOption Options.ownerId
            |> addOption Options.doNotSwitch

        ownerCreateCommand.Action <- new Create()
        ownerCommand.Subcommands.Add(ownerCreateCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the owner.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        getCommand.Action <- new Get()
        ownerCommand.Subcommands.Add(getCommand)

        let setNameCommand =
            new Command("set-name", Description = "Change the name of the owner.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Action <- new SetName()
        ownerCommand.Subcommands.Add(setNameCommand)

        let setTypeCommand =
            new Command("set-type", Description = "Change the type of the owner.")
            |> addOption Options.ownerTypeRequired
            |> addCommonOptions

        setTypeCommand.Action <- new SetType()
        ownerCommand.Subcommands.Add(setTypeCommand)

        let setSearchVisibilityCommand =
            new Command("set-search-visibility", Description = "Change the search visibility of the owner.")
            |> addOption Options.searchVisibilityRequired
            |> addCommonOptions

        setSearchVisibilityCommand.Action <- new SetSearchVisibility()
        ownerCommand.Subcommands.Add(setSearchVisibilityCommand)

        let setDescriptionCommand =
            new Command("set-description", Description = "Change the description of the owner.")
            |> addOption Options.descriptionRequired
            |> addCommonOptions

        setDescriptionCommand.Action <- new SetDescription()
        ownerCommand.Subcommands.Add(setDescriptionCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the owner.")
            |> addOption Options.force
            |> addOption Options.deleteReason
            |> addCommonOptions

        deleteCommand.Action <- new Delete()
        ownerCommand.Subcommands.Add(deleteCommand)

        let undeleteCommand =
            new Command("undelete", Description = "Undelete a deleted owner.")
            |> addCommonOptions

        undeleteCommand.Action <- new Undelete()
        ownerCommand.Subcommands.Add(undeleteCommand)

        ownerCommand
