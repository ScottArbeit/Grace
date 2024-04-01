namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Connect
open System
open System.Collections.Generic
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Threading
open System.CommandLine
open Spectre.Console

module Connect =

    type CommonParameters() =
        inherit ParameterBase()
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RetrieveDefaultBranch: bool = true with get, set

    module private Options =
        let repositoryId =
            new Option<String>([| "--repositoryId"; "-r" |], IsRequired = false, Description = "The repository's ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let repositoryName =
            new Option<String>(
                [| "--repositoryName"; "-n" |],
                IsRequired = false,
                Description = "The name of the repository.",
                Arity = ArgumentArity.ExactlyOne
            )

        let ownerId =
            new Option<String>("--ownerId", IsRequired = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let ownerName =
            new Option<String>("--ownerName", IsRequired = false, Description = "The repository's owner name.", Arity = ArgumentArity.ExactlyOne)

        let organizationId =
            new Option<String>(
                "--organizationId",
                IsRequired = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationName =
            new Option<String>("--organizationName", IsRequired = false, Description = "The repository's organization name.", Arity = ArgumentArity.ZeroOrOne)

        let correlationId =
            new Option<String>(
                [| "--correlationId"; "-c" |],
                IsRequired = false,
                Description = "CorrelationId to track this command throughout Grace. [default: new Guid]",
                Arity = ArgumentArity.ExactlyOne
            )

        let serverAddress =
            new Option<String>(
                [| "--serverAddress"; "-s" |],
                IsRequired = false,
                Description = "Address of the Grace server to connect to.",
                Arity = ArgumentArity.ExactlyOne
            )

        let retrieveDefaultBranch =
            new Option<bool>(
                [| "--retrieveDefaultBranch" |],
                IsRequired = false,
                Description = "True to retrieve the default branch after connecting; false to connect but not download any files.",
                Arity = ArgumentArity.ExactlyOne
            )

    let private ValidateIncomingParameters (parseResult: ParseResult) commonParameters =

        let ``RepositoryId must be a non-empty Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable repositoryId: Guid = Guid.Empty

            if parseResult.HasOption(Options.repositoryId) then
                match
                    (Guid.isValidAndNotEmpty commonParameters.RepositoryId InvalidRepositoryId)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``RepositoryName must be valid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.HasOption(Options.repositoryName) then
                match
                    (String.isValidGraceName commonParameters.RepositoryName InvalidRepositoryName)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OwnerId must be a non-empty Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable ownerId: Guid = Guid.Empty

            if parseResult.HasOption(Options.ownerId) then
                match (Guid.isValidAndNotEmpty commonParameters.OwnerId InvalidOwnerId).Result with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OwnerName must be valid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.HasOption(Options.ownerName) then
                match (String.isValidGraceName commonParameters.OwnerName InvalidOwnerName).Result with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OrganizationId must be a non-empty Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            let mutable organizationId: Guid = Guid.Empty

            if parseResult.HasOption(Options.organizationId) then
                match
                    (Guid.isValidAndNotEmpty commonParameters.OrganizationId InvalidOrganizationId)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        let ``OrganizationName must be valid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if parseResult.HasOption(Options.organizationName) then
                match
                    (String.isValidGraceName commonParameters.OrganizationName InvalidOrganizationName)
                        .Result
                with
                | Ok result -> Result.Ok(parseResult, commonParameters)
                | Error error -> Result.Error error
            else
                Result.Ok(parseResult, commonParameters)

        (parseResult, commonParameters)
        |> ``RepositoryId must be a non-empty Guid``
        >>= ``RepositoryName must be valid``
        >>= ``OwnerId must be a non-empty Guid``
        >>= ``OwnerName must be valid``
        >>= ``OrganizationId must be a non-empty Guid``
        >>= ``OrganizationName must be valid``

    let private Connect =
        CommandHandler.Create(fun (parseResult: ParseResult) (commonParameters: CommonParameters) ->
            try
                if parseResult |> verbose then
                    printParseResult parseResult

                let validateIncomingParameters =
                    ValidateIncomingParameters parseResult commonParameters

                match validateIncomingParameters with
                | Result.Ok r -> printfn ("ok")
                | Result.Error error -> printfn ($"error: {Utilities.getDiscriminatedUnionFullName (error)}")

                printfn ($"Fake result: {parseResult}")
            with :? OperationCanceledException as ex ->
                printfn ($"Operation cancelled: {ex.Message}"))

    let Build =
        // Create main command and aliases, if any.
        let connectCommand =
            new Command("connect", Description = "Connect to a Grace repository.")

        connectCommand.AddOption(Options.repositoryId)
        connectCommand.AddOption(Options.repositoryName)
        connectCommand.AddOption(Options.ownerId)
        connectCommand.AddOption(Options.ownerName)
        connectCommand.AddOption(Options.organizationId)
        connectCommand.AddOption(Options.organizationName)
        connectCommand.AddOption(Options.correlationId)
        connectCommand.AddOption(Options.serverAddress)
        connectCommand.AddOption(Options.retrieveDefaultBranch)

        connectCommand.Handler <- Connect
        connectCommand
