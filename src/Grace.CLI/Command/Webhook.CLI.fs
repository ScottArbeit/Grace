namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open Grace.Types.Webhooks
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

module WebhookCommand =
    module private Options =
        let webhookId =
            new Option<string>(
                "--webhook",
                [|
                    "--webhook-id"
                    "--rule"
                    "--rule-id"
                |],
                Required = true,
                Description = "The webhook rule ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let deliveryId =
            new Option<string>(
                "--delivery",
                [|
                    "--delivery-id"
                    "--webhook-delivery"
                    "--webhook-delivery-id"
                |],
                Required = true,
                Description = "The webhook delivery ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let name = new Option<string>("--name", Required = false, Description = "The webhook display name.", Arity = ArgumentArity.ExactlyOne)

        let eventName =
            new Option<string>("--event", [| "--event-name" |], Required = true, Description = "The webhook event name.", Arity = ArgumentArity.ExactlyOne)

        let eventVersion =
            new Option<int>(
                "--event-version",
                Required = false,
                Description = "The webhook event version.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 1)
            )

        let url = new Option<string>("--url", Required = true, Description = "The destination URL.", Arity = ArgumentArity.ExactlyOne)

        let urlOptional = new Option<string>("--url", Required = false, Description = "The destination URL.", Arity = ArgumentArity.ExactlyOne)

        let allowUnsafeLocal =
            new Option<bool>(
                "--allow-unsafe-local-development",
                [|
                    "--acknowledge-unsafe-local-development"
                |],
                Required = false,
                Description = "Allow unsafe local-development webhook URLs.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let signingSecretVersion =
            new Option<string>(
                "--signing-secret-version",
                Required = false,
                Description = "Signing secret version used for this webhook.",
                Arity = ArgumentArity.ExactlyOne
            )

        let maxAttempts =
            new Option<int>(
                "--max-attempts",
                Required = false,
                Description = "Maximum delivery attempts.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> WebhookRetryPolicy.Default.MaxAttempts)
            )

        let initialDelaySeconds =
            new Option<int>(
                "--initial-delay-seconds",
                Required = false,
                Description = "Initial retry delay in seconds.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> WebhookRetryPolicy.Default.InitialDelaySeconds)
            )

        let maxDelaySeconds =
            new Option<int>(
                "--max-delay-seconds",
                Required = false,
                Description = "Maximum retry delay in seconds.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> WebhookRetryPolicy.Default.MaxDelaySeconds)
            )

        let targetBranchId =
            new Option<BranchId>(
                OptionName.BranchId,
                Required = false,
                Description = "Optional target branch ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> BranchId.Empty)
            )

        let includeDeleted =
            new Option<bool>(
                "--include-deleted",
                Required = false,
                Description = "Include deleted webhook rules.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let includeTerminal =
            new Option<bool>(
                "--include-terminal",
                Required = false,
                Description = "Include terminal deliveries.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> true)
            )

        let dedupeKey =
            new Option<string>("--dedupe-key", Required = false, Description = "Optional test delivery dedupe key.", Arity = ArgumentArity.ExactlyOne)

        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<string>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The organization's ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OrganizationId.Empty)
            )

        let organizationName =
            new Option<string>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The organization's name. [default: current organization]",
                Arity = ArgumentArity.ExactlyOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> RepositoryId.Empty)
            )

        let repositoryName =
            new Option<string>(
                OptionName.RepositoryName,
                Required = false,
                Description = "The repository's name. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

    let private noValue (value: string) = if String.IsNullOrWhiteSpace value then String.Empty else value

    let private targetBranch (parseResult: ParseResult) =
        let value = parseResult.GetValue(Options.targetBranchId)
        if value = BranchId.Empty then String.Empty else value.ToString()

    let private urlSafety allowUnsafe =
        if allowUnsafe then
            OutboundUrlSafety.LocalUnsafeDevOnly
        else
            OutboundUrlSafety.PublicHttps

    let private addScope (parameters: #Grace.Shared.Parameters.Webhook.WebhookRuleParameters) (graceIds: GraceIds) (parseResult: ParseResult) =
        parameters.OwnerId <- graceIds.OwnerIdString
        parameters.OwnerName <- graceIds.OwnerName
        parameters.OrganizationId <- graceIds.OrganizationIdString
        parameters.OrganizationName <- graceIds.OrganizationName
        parameters.RepositoryId <- graceIds.RepositoryIdString
        parameters.RepositoryName <- graceIds.RepositoryName
        parameters.TargetBranchId <- targetBranch parseResult
        parameters.CorrelationId <- graceIds.CorrelationId
        parameters

    let private addDeliveryScope (parameters: #Grace.Shared.Parameters.Webhook.WebhookDeliveryParameters) (graceIds: GraceIds) (parseResult: ParseResult) =
        parameters.OwnerId <- graceIds.OwnerIdString
        parameters.OwnerName <- graceIds.OwnerName
        parameters.OrganizationId <- graceIds.OrganizationIdString
        parameters.OrganizationName <- graceIds.OrganizationName
        parameters.RepositoryId <- graceIds.RepositoryIdString
        parameters.RepositoryName <- graceIds.RepositoryName
        parameters.TargetBranchId <- targetBranch parseResult
        parameters.CorrelationId <- graceIds.CorrelationId
        parameters

    let internal renderWebhookRule (parseResult: ParseResult) (rule: WebhookRule) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("WebhookRuleId", Markup.Escape(rule.WebhookRuleId.ToString()))
            |> ignore

            table.AddRow("Name", Markup.Escape(rule.Name))
            |> ignore

            table.AddRow("Event", Markup.Escape($"{rule.EventName} v{rule.EventVersion}"))
            |> ignore

            table.AddRow("Status", Markup.Escape(getDiscriminatedUnionCaseName rule.Status))
            |> ignore

            table.AddRow("UrlSafety", Markup.Escape(getDiscriminatedUnionCaseName rule.Url.Safety))
            |> ignore

            table.AddRow("MaxAttempts", rule.RetryPolicy.MaxAttempts.ToString())
            |> ignore

            AnsiConsole.Write(table)

    let internal renderWebhookRules (parseResult: ParseResult) (rules: WebhookRule seq) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("WebhookRuleId") |> ignore
            table.AddColumn("Name") |> ignore
            table.AddColumn("Event") |> ignore
            table.AddColumn("Status") |> ignore

            rules
            |> Seq.iter (fun rule ->
                table.AddRow(
                    Markup.Escape(rule.WebhookRuleId.ToString()),
                    Markup.Escape(rule.Name),
                    Markup.Escape($"{rule.EventName} v{rule.EventVersion}"),
                    Markup.Escape(getDiscriminatedUnionCaseName rule.Status)
                )
                |> ignore)

            AnsiConsole.Write(table)

    let internal renderWebhookDelivery (parseResult: ParseResult) (delivery: WebhookDelivery) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("Field") |> ignore
            table.AddColumn("Value") |> ignore

            table.AddRow("DeliveryId", Markup.Escape(delivery.WebhookDeliveryId.ToString()))
            |> ignore

            table.AddRow("WebhookRuleId", Markup.Escape(delivery.WebhookRuleId.ToString()))
            |> ignore

            table.AddRow("Event", Markup.Escape($"{delivery.EventName} v{delivery.EventVersion}"))
            |> ignore

            table.AddRow("AttemptCount", delivery.AttemptCount.ToString())
            |> ignore

            table.AddRow("Status", Markup.Escape(getDiscriminatedUnionCaseName delivery.Status))
            |> ignore

            table.AddRow(
                "NextRetry",
                delivery.NextAttemptAt
                |> Option.map string
                |> Option.defaultValue "-"
            )
            |> ignore

            table.AddRow(
                "LastStatusCode",
                delivery.LastStatusCode
                |> Option.map string
                |> Option.defaultValue "-"
            )
            |> ignore

            table.AddRow(
                "FailureDetails",
                delivery.LastError
                |> Option.defaultValue "-"
                |> Markup.Escape
            )
            |> ignore

            AnsiConsole.Write(table)

    let internal renderWebhookDeliveries (parseResult: ParseResult) (deliveries: WebhookDelivery seq) =
        if
            not (parseResult |> json)
            && not (parseResult |> silent)
        then
            let table = Table(Border = TableBorder.Rounded)
            table.AddColumn("DeliveryId") |> ignore
            table.AddColumn("WebhookRuleId") |> ignore
            table.AddColumn("Event") |> ignore
            table.AddColumn("Attempts") |> ignore
            table.AddColumn("Status") |> ignore
            table.AddColumn("NextRetry") |> ignore

            deliveries
            |> Seq.iter (fun delivery ->
                table.AddRow(
                    Markup.Escape(delivery.WebhookDeliveryId.ToString()),
                    Markup.Escape(delivery.WebhookRuleId.ToString()),
                    Markup.Escape($"{delivery.EventName} v{delivery.EventVersion}"),
                    delivery.AttemptCount.ToString(),
                    Markup.Escape(getDiscriminatedUnionCaseName delivery.Status),
                    delivery.NextAttemptAt
                    |> Option.map string
                    |> Option.defaultValue "-"
                )
                |> ignore)

            AnsiConsole.Write(table)

    let private execute action render =
        task {
            try
                let! result = action ()

                match result with
                | Ok returnValue ->
                    render returnValue.ReturnValue
                    return Ok returnValue
                | Error error -> return Error error
            with
            | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}" String.Empty)
        }

    let private createHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let allowUnsafe = parseResult.GetValue(Options.allowUnsafeLocal)

        let parameters = Grace.Shared.Parameters.Webhook.CreateWebhookRuleParameters()
        parameters.WebhookRuleId <- Guid.NewGuid().ToString()

        parameters.Name <-
            parseResult.GetValue(Options.name)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.EventName <- parseResult.GetValue(Options.eventName)
        parameters.EventVersion <- parseResult.GetValue(Options.eventVersion)
        parameters.Url <- parseResult.GetValue(Options.url)
        parameters.UrlSafety <- urlSafety allowUnsafe
        parameters.AcknowledgeUnsafeLocalDevelopment <- allowUnsafe

        parameters.SigningSecretVersion <-
            parseResult.GetValue(Options.signingSecretVersion)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.MaxAttempts <- parseResult.GetValue(Options.maxAttempts)
        parameters.InitialDelaySeconds <- parseResult.GetValue(Options.initialDelaySeconds)
        parameters.MaxDelaySeconds <- parseResult.GetValue(Options.maxDelaySeconds)

        addScope parameters graceIds parseResult |> ignore
        execute (fun () -> Grace.SDK.WebhookRule.Create parameters) (renderWebhookRule parseResult)

    let private listHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Webhook.ListWebhookRulesParameters(IncludeDeleted = parseResult.GetValue(Options.includeDeleted))

        addScope parameters graceIds parseResult |> ignore
        execute (fun () -> Grace.SDK.WebhookRule.List parameters) (renderWebhookRules parseResult)

    let private showHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = Grace.Shared.Parameters.Webhook.ShowWebhookRuleParameters(WebhookRuleId = parseResult.GetValue(Options.webhookId))
        addScope parameters graceIds parseResult |> ignore
        execute (fun () -> Grace.SDK.WebhookRule.Show parameters) (renderWebhookRule parseResult)

    let private updateHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let allowUnsafe = parseResult.GetValue(Options.allowUnsafeLocal)

        let parameters = Grace.Shared.Parameters.Webhook.UpdateWebhookRuleParameters()
        parameters.WebhookRuleId <- parseResult.GetValue(Options.webhookId)

        parameters.Name <-
            parseResult.GetValue(Options.name)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.EventName <- parseResult.GetValue(Options.eventName)
        parameters.EventVersion <- parseResult.GetValue(Options.eventVersion)

        parameters.Url <-
            parseResult.GetValue(Options.urlOptional)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.UrlSafety <- urlSafety allowUnsafe
        parameters.AcknowledgeUnsafeLocalDevelopment <- allowUnsafe

        parameters.SigningSecretVersion <-
            parseResult.GetValue(Options.signingSecretVersion)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.MaxAttempts <- parseResult.GetValue(Options.maxAttempts)
        parameters.InitialDelaySeconds <- parseResult.GetValue(Options.initialDelaySeconds)
        parameters.MaxDelaySeconds <- parseResult.GetValue(Options.maxDelaySeconds)

        addScope parameters graceIds parseResult |> ignore
        execute (fun () -> Grace.SDK.WebhookRule.Update parameters) (renderWebhookRule parseResult)

    let private simpleRuleHandler makeParameters sdkCall parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames
        let parameters = makeParameters (parseResult.GetValue(Options.webhookId))
        addScope parameters graceIds parseResult |> ignore
        execute (fun () -> sdkCall parameters) (renderWebhookRule parseResult)

    let private testHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Webhook.TestWebhookRuleParameters()
        parameters.WebhookRuleId <- parseResult.GetValue(Options.webhookId)

        parameters.DedupeKey <-
            parseResult.GetValue(Options.dedupeKey)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        addScope parameters graceIds parseResult |> ignore
        execute (fun () -> Grace.SDK.WebhookRule.Test parameters) (renderWebhookDelivery parseResult)

    let private deliveriesHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Webhook.ListWebhookDeliveriesParameters()

        parameters.WebhookRuleId <-
            parseResult.GetValue(Options.webhookId)
            |> Option.ofObj
            |> Option.defaultValue String.Empty

        parameters.IncludeTerminal <- parseResult.GetValue(Options.includeTerminal)

        addDeliveryScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.WebhookDelivery.List parameters) (renderWebhookDeliveries parseResult)

    let private deliveryShowHandler parseResult =
        let graceIds = parseResult |> getNormalizedIdsAndNames

        let parameters = Grace.Shared.Parameters.Webhook.ShowWebhookDeliveryParameters(WebhookDeliveryId = parseResult.GetValue(Options.deliveryId))

        addDeliveryScope parameters graceIds parseResult
        |> ignore

        execute (fun () -> Grace.SDK.WebhookDelivery.Show parameters) (renderWebhookDelivery parseResult)

    type Action<'T>(handler: ParseResult -> Task<GraceResult<'T>>) =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, _: CancellationToken) : Task<int> =
            task {
                let! result = handler parseResult
                return result |> renderOutput parseResult
            }

    let private action<'T> (handler: ParseResult -> Task<GraceResult<'T>>) = Action<'T>(handler)

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId
            |> addOption Options.targetBranchId

        let webhookCommand = new Command("webhook", Description = "Manage webhook rules and deliveries.")

        let createCommand =
            new Command("create", Description = "Create a webhook rule.")
            |> addOption Options.name
            |> addOption Options.eventName
            |> addOption Options.eventVersion
            |> addOption Options.url
            |> addOption Options.allowUnsafeLocal
            |> addOption Options.signingSecretVersion
            |> addOption Options.maxAttempts
            |> addOption Options.initialDelaySeconds
            |> addOption Options.maxDelaySeconds
            |> addCommonOptions

        createCommand.Action <- action createHandler
        webhookCommand.Subcommands.Add(createCommand)

        let listCommand =
            new Command("list", Description = "List webhook rules.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        listCommand.Action <- action listHandler
        webhookCommand.Subcommands.Add(listCommand)

        let showCommand =
            new Command("show", Description = "Show a webhook rule.")
            |> addOption Options.webhookId
            |> addCommonOptions

        showCommand.Action <- action showHandler
        webhookCommand.Subcommands.Add(showCommand)

        let updateCommand =
            new Command("update", Description = "Update a webhook rule.")
            |> addOption Options.webhookId
            |> addOption Options.name
            |> addOption Options.eventName
            |> addOption Options.eventVersion
            |> addOption Options.urlOptional
            |> addOption Options.allowUnsafeLocal
            |> addOption Options.signingSecretVersion
            |> addOption Options.maxAttempts
            |> addOption Options.initialDelaySeconds
            |> addOption Options.maxDelaySeconds
            |> addCommonOptions

        updateCommand.Action <- action updateHandler
        webhookCommand.Subcommands.Add(updateCommand)

        let enableCommand =
            new Command("enable", Description = "Enable a webhook rule.")
            |> addOption Options.webhookId
            |> addCommonOptions

        enableCommand.Action <-
            action (simpleRuleHandler (fun id -> Grace.Shared.Parameters.Webhook.EnableWebhookRuleParameters(WebhookRuleId = id)) Grace.SDK.WebhookRule.Enable)

        webhookCommand.Subcommands.Add(enableCommand)

        let disableCommand =
            new Command("disable", Description = "Disable a webhook rule.")
            |> addOption Options.webhookId
            |> addCommonOptions

        disableCommand.Action <-
            action (
                simpleRuleHandler (fun id -> Grace.Shared.Parameters.Webhook.DisableWebhookRuleParameters(WebhookRuleId = id)) Grace.SDK.WebhookRule.Disable
            )

        webhookCommand.Subcommands.Add(disableCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete a webhook rule.")
            |> addOption Options.webhookId
            |> addCommonOptions

        deleteCommand.Action <-
            action (simpleRuleHandler (fun id -> Grace.Shared.Parameters.Webhook.DeleteWebhookRuleParameters(WebhookRuleId = id)) Grace.SDK.WebhookRule.Delete)

        webhookCommand.Subcommands.Add(deleteCommand)

        let testCommand =
            new Command("test", Description = "Create a test webhook delivery.")
            |> addOption Options.webhookId
            |> addOption Options.dedupeKey
            |> addCommonOptions

        testCommand.Action <- action testHandler
        webhookCommand.Subcommands.Add(testCommand)

        let deliveriesCommand =
            new Command("deliveries", Description = "List webhook deliveries.")
            |> addOption Options.webhookId
            |> addOption Options.includeTerminal
            |> addCommonOptions

        deliveriesCommand.Action <- action deliveriesHandler
        webhookCommand.Subcommands.Add(deliveriesCommand)

        let deliveryCommand = new Command("delivery", Description = "Inspect one webhook delivery.")

        let deliveryShowCommand =
            new Command("show", Description = "Show a webhook delivery.")
            |> addOption Options.deliveryId
            |> addCommonOptions

        deliveryShowCommand.Action <- action deliveryShowHandler
        deliveryCommand.Subcommands.Add(deliveryShowCommand)
        webhookCommand.Subcommands.Add(deliveryCommand)

        webhookCommand
