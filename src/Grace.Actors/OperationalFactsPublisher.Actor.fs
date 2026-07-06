namespace Grace.Actors

open Azure.Messaging.ServiceBus
open Grace.Actors.Context
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Extensions.Logging
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Publishes immutable operational usage facts without coupling them to GraceEvent.
module OperationalFactsPublisher =
    /// The JSON media type used for operational usage fact Service Bus messages.
    [<Literal>]
    let JsonContentType = "application/json"

    /// The Service Bus subject used to distinguish operational usage fact messages.
    [<Literal>]
    let UsageFactSubject = "GraceOperationalUsageFact"

    /// The application property that identifies the operational fact contract carried by the message.
    [<Literal>]
    let UsageFactMessageTypeProperty = "graceMessageType"

    /// The application property value for operational usage fact messages.
    [<Literal>]
    let UsageFactMessageType = "UsageFact"

    /// The application property that records the specific usage fact kind without parsing the payload.
    [<Literal>]
    let UsageFactKindProperty = "usageFactKind"

    /// Carries the Service Bus topic and message envelope produced for one usage fact.
    type UsageFactPublication = { TopicName: string; Message: ServiceBusMessage }

    let private defaultAzureCredential = lazy (Azure.Identity.DefaultAzureCredential())

    let private serviceBusClient =
        lazy
            let settings =
                pubSubSettings.AzureServiceBus
                |> Option.defaultWith (fun () -> invalidOp "Azure Service Bus settings are required before publishing operational usage facts.")

            if settings.UseManagedIdentity then
                let fullyQualifiedNamespace =
                    if not (String.IsNullOrWhiteSpace settings.FullyQualifiedNamespace) then
                        settings.FullyQualifiedNamespace
                    else
                        AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Service Bus namespace is required for managed identity.")

                logToConsole $"Creating operational facts ServiceBusClient with Managed Identity for namespace: {fullyQualifiedNamespace}."
                ServiceBusClient(fullyQualifiedNamespace, defaultAzureCredential.Value)
            else
                logToConsole "Creating operational facts ServiceBusClient with connection string."
                ServiceBusClient(settings.ConnectionString)

    /// Resolves the required Service Bus topic for operational usage facts.
    let internal resolveOperationalFactsTopicName () =
        let environmentVariableName = EnvironmentVariables.AzureServiceBusOperationalFactsTopic
        let topicName = Environment.GetEnvironmentVariable environmentVariableName

        if String.IsNullOrWhiteSpace topicName then
            invalidOp (sprintf "Environment variable '%s' must be set when publishing operational usage facts to Azure Service Bus." environmentVariableName)

        topicName

    let private operationalFactsSender = lazy (serviceBusClient.Value.CreateSender(resolveOperationalFactsTopicName ()))

    /// Builds the Service Bus message envelope for a serialized UsageFact payload.
    let internal createMessage (usageFact: UsageFact) =
        match UsageFact.Validate usageFact with
        | Ok _ ->
            let payload = JsonSerializer.SerializeToUtf8Bytes(usageFact, Constants.JsonSerializerOptions)
            let message = ServiceBusMessage(payload)
            message.ContentType <- JsonContentType
            message.Subject <- UsageFactSubject
            message.MessageId <- usageFact.UsageFactId.ToString("D")
            message.CorrelationId <- usageFact.CorrelationId
            message.ApplicationProperties[ UsageFactMessageTypeProperty ] <- UsageFactMessageType
            message.ApplicationProperties[ UsageFactKindProperty ] <- usageFact.FactKind.ToString()
            message
        | Error errors ->
            let validationErrors = String.Join("; ", errors)
            invalidArg (nameof usageFact) $"UsageFact failed validation: {validationErrors}"

    /// Builds the topic and Service Bus message envelope for one operational usage fact.
    let internal createPublication (topicName: string) (usageFact: UsageFact) =
        if String.IsNullOrWhiteSpace topicName then
            invalidArg (nameof topicName) "Operational usage facts topic name is required."

        { TopicName = topicName; Message = createMessage usageFact }

    let private tryGetLogger () =
        if isNull loggerFactory then
            None
        else
            Some(loggerFactory.CreateLogger("OperationalFactsPublisher.Actor"))

    /// Describes unsupported operational fact publication providers without including the usage payload.
    let private unsupportedPubSubProviderMessage pubSubSystem =
        $"Grace pub-sub system {getDiscriminatedUnionCaseName pubSubSystem} cannot publish operational UsageFacts. Configure AzureServiceBus or disable operational fact publication explicitly."

    /// Raises the fail-closed configuration error used when a configured provider cannot publish usage facts.
    let internal rejectUnsupportedPubSubProvider pubSubSystem = invalidOp (unsupportedPubSubProviderMessage pubSubSystem)

    /// Publishes an immutable UsageFact to the dedicated operational facts Service Bus topic.
    let publishUsageFact (usageFact: UsageFact) (cancellationToken: CancellationToken) =
        task {
            match pubSubSettings.System with
            | GracePubSubSystem.AzureServiceBus ->
                let topicName = resolveOperationalFactsTopicName ()
                let publication = createPublication topicName usageFact

                do! operationalFactsSender.Value.SendMessageAsync(publication.Message, cancellationToken)

                match tryGetLogger () with
                | Some log ->
                    log.LogInformation(
                        "{CurrentInstant}: Published operational UsageFact via Azure Service Bus. UsageFactId: {UsageFactId}; CorrelationId: {CorrelationId}; FactKind: {FactKind}; Topic: {Topic}.",
                        getCurrentInstantExtended (),
                        usageFact.UsageFactId,
                        usageFact.CorrelationId,
                        usageFact.FactKind,
                        topicName
                    )
                | None -> ()
            | GracePubSubSystem.UnknownPubSubProvider ->
                match tryGetLogger () with
                | Some log ->
                    log.LogDebug(
                        "Pub-sub system disabled; dropping operational UsageFact {UsageFactId} with CorrelationId: {CorrelationId}.",
                        usageFact.UsageFactId,
                        usageFact.CorrelationId
                    )
                | None -> ()
            | otherSystem ->
                let message = unsupportedPubSubProviderMessage otherSystem

                match tryGetLogger () with
                | Some log -> log.LogError("{Message} System: {System}.", message, getDiscriminatedUnionCaseName otherSystem)
                | None -> ()

                rejectUnsupportedPubSubProvider otherSystem
        }
        :> Task
