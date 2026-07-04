namespace Grace.Operations.Worker

open Azure.Core
open Azure.Identity
open Azure.Messaging.ServiceBus
open Grace.Operations.Data
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Usage
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Identifies the Grace operations worker assembly before ingestion hosting is wired by a later slice.
[<AbstractClass; Sealed>]
type internal OperationsWorkerAssembly =
    class
    end

/// Holds the operational fact envelope constants shared with the publisher contract without depending on Grace.Actors.
[<RequireQualifiedAccess>]
module internal OperationalFactEnvelope =

    /// Identifies the Service Bus subject used for immutable usage fact messages.
    [<Literal>]
    let UsageFactSubject = "GraceOperationalUsageFact"

    /// Names the application property that classifies operational fact messages.
    [<Literal>]
    let UsageFactMessageTypeProperty = "graceMessageType"

    /// Identifies the only operational fact message type consumed by this worker slice.
    [<Literal>]
    let UsageFactMessageType = "UsageFact"

    /// Names the application property that records the usage fact kind for diagnostics and routing.
    [<Literal>]
    let UsageFactKindProperty = "usageFactKind"

/// Carries the safe metadata and body bytes the ingestion processor needs from a Service Bus message.
type OperationsUsageMessage =
    {
        MessageId: string
        CorrelationId: string
        DeliveryCount: int
        Subject: string
        ApplicationProperties: IReadOnlyDictionary<string, obj>
        Body: byte array
    }

/// Settles an operational usage message only after the processor decides the durable outcome.
type IOperationsUsageMessageActions =

    /// Completes a message after durable SQL processing succeeds or proves the fact was already processed.
    abstract CompleteAsync: cancellationToken: CancellationToken -> Task

    /// Abandons a message when a transient failure should allow Service Bus retry delivery.
    abstract AbandonAsync: cancellationToken: CancellationToken -> Task

    /// Dead-letters a message that cannot become a valid usage fact through retry.
    abstract DeadLetterAsync: reason: string * description: string * cancellationToken: CancellationToken -> Task

/// Stores one usage fact through the operations data layer.
type IOperationsUsageFactStore =

    /// Persists the fact idempotently and returns the operations data-layer result.
    abstract StoreUsageFactAsync: fact: UsageFact * cancellationToken: CancellationToken -> Task<Result<UsageFactPersistenceResult, string list>>

/// Adapts the concrete operations data store to the worker's fakeable ingestion dependency.
type OperationsUsageFactStoreAdapter(store: OperationsUsageStore) =

    interface IOperationsUsageFactStore with

        member _.StoreUsageFactAsync(fact, cancellationToken) = store.StoreUsageFactAsync(fact, cancellationToken)

/// Reads Service Bus settings required by the operational usage ingestion worker.
type OperationsWorkerSettings =
    {
        TopicName: string
        SubscriptionName: string
        SqlConnectionString: string
        ServiceBusConnectionString: string option
        ServiceBusFullyQualifiedNamespace: string option
        MaxConcurrentCalls: int
        PrefetchCount: int
    }

/// Resolves worker settings from host configuration and environment variables.
[<RequireQualifiedAccess>]
module OperationsWorkerSettings =

    /// The durable subscription used by the operations worker for the operational facts topic.
    [<Literal>]
    let DefaultProcessorSubscriptionName = "operational-facts-processor"

    /// The environment variable that confirms the durable operational facts processor subscription name.
    [<Literal>]
    let ProcessorSubscriptionEnvironmentVariable = "grace__azure_service_bus__operational_facts_processor_subscription"

    /// The environment variable that contains the SQL connection string for operations usage storage.
    [<Literal>]
    let SqlConnectionStringEnvironmentVariable = "grace__operations__sql__connectionstring"

    /// The environment variable that controls Service Bus processor concurrency for usage fact ingestion.
    [<Literal>]
    let MaxConcurrentCallsEnvironmentVariable = "grace__operations_worker__max_concurrent_calls"

    /// The environment variable that controls Service Bus prefetch for usage fact ingestion.
    [<Literal>]
    let PrefetchCountEnvironmentVariable = "grace__operations_worker__prefetch_count"

    /// Returns a trimmed setting value when configuration contains a non-empty entry.
    let private optionalSetting (configuration: IConfiguration) name =
        [
            configuration[getConfigKey name]
            configuration[name]
            Environment.GetEnvironmentVariable name
        ]
        |> List.tryPick (fun value -> if String.IsNullOrWhiteSpace value then None else Some(value.Trim()))

    /// Reads a setting or returns the supplied default.
    let private settingOrDefault configuration name defaultValue =
        optionalSetting configuration name
        |> Option.defaultValue defaultValue

    /// Reads a positive integer setting or returns the supplied default.
    let private positiveIntSetting configuration name defaultValue =
        match optionalSetting configuration name with
        | Some value ->
            match Int32.TryParse value with
            | true, parsed when parsed > 0 -> parsed
            | _ -> defaultValue
        | None -> defaultValue

    /// Attempts managed-identity Service Bus discovery without letting unrelated Azure environment gaps abort validation.
    let private serviceBusNamespaceFromAzureEnvironment () =
        try
            AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
        with
        | :? InvalidOperationException
        | :? TypeInitializationException -> None

    /// Builds validated worker settings from configuration.
    let fromConfiguration (configuration: IConfiguration) =
        let topicName = settingOrDefault configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic Constants.GraceOperationalFactsTopic

        let subscriptionName = settingOrDefault configuration ProcessorSubscriptionEnvironmentVariable DefaultProcessorSubscriptionName

        let sqlConnectionString = optionalSetting configuration SqlConnectionStringEnvironmentVariable

        let serviceBusConnectionString = optionalSetting configuration Constants.EnvironmentVariables.AzureServiceBusConnectionString

        let serviceBusNamespace = optionalSetting configuration Constants.EnvironmentVariables.AzureServiceBusNamespace

        let errors = ResizeArray<string>()

        if String.IsNullOrWhiteSpace topicName then
            errors.Add($"{Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic} is required.")

        if subscriptionName
           <> DefaultProcessorSubscriptionName then
            errors.Add(
                $"{ProcessorSubscriptionEnvironmentVariable} must be '{DefaultProcessorSubscriptionName}' so the worker uses the durable operational facts processor subscription."
            )

        if sqlConnectionString.IsNone then
            errors.Add($"{SqlConnectionStringEnvironmentVariable} is required.")

        if serviceBusConnectionString.IsNone
           && serviceBusNamespace.IsNone
           && serviceBusNamespaceFromAzureEnvironment().IsNone then
            errors.Add(
                $"{Constants.EnvironmentVariables.AzureServiceBusConnectionString} or {Constants.EnvironmentVariables.AzureServiceBusNamespace} is required."
            )

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok
                {
                    TopicName = topicName
                    SubscriptionName = subscriptionName
                    SqlConnectionString = sqlConnectionString.Value
                    ServiceBusConnectionString = serviceBusConnectionString
                    ServiceBusFullyQualifiedNamespace =
                        match serviceBusConnectionString with
                        | Some _ -> serviceBusNamespace
                        | None ->
                            serviceBusNamespace
                            |> Option.orElseWith serviceBusNamespaceFromAzureEnvironment
                    MaxConcurrentCalls = positiveIntSetting configuration MaxConcurrentCallsEnvironmentVariable 4
                    PrefetchCount = positiveIntSetting configuration PrefetchCountEnvironmentVariable 16
                }

/// Handles one usage fact message through validation, SQL persistence, and explicit Service Bus settlement.
type OperationsUsageIngestionProcessor(store: IOperationsUsageFactStore, logger: ILogger<OperationsUsageIngestionProcessor>) =

    /// Reads a string application property without exposing untrusted payload values in logs.
    let tryGetStringProperty propertyName (properties: IReadOnlyDictionary<string, obj>) =
        match properties.TryGetValue propertyName with
        | true, (:? string as value) when not (String.IsNullOrWhiteSpace value) -> Some value
        | true, value when not (isNull value) -> Some(string value)
        | _ -> None

    /// Builds a bounded diagnostic description without including the message body.
    let describeErrors (errors: string list) = String.Join("; ", errors)

    /// Dead-letters an invalid usage message with deterministic reason metadata.
    let deadLetterAsync reason description (actions: IOperationsUsageMessageActions) cancellationToken =
        actions.DeadLetterAsync(reason, description, cancellationToken)

    /// Deserializes a usage fact using the shared Grace JSON contract options.
    let deserializeUsageFact (body: byte array) =
        use stream = new MemoryStream(body)
        JsonSerializer.Deserialize<UsageFact>(stream, Constants.JsonSerializerOptions)

    /// Processes a usage fact message and settles it only after the durable outcome is known.
    member _.ProcessMessageAsync(message: OperationsUsageMessage, actions: IOperationsUsageMessageActions, cancellationToken: CancellationToken) =
        task {
            try
                cancellationToken.ThrowIfCancellationRequested()

                let messageType = tryGetStringProperty OperationalFactEnvelope.UsageFactMessageTypeProperty message.ApplicationProperties

                if message.Subject
                   <> OperationalFactEnvelope.UsageFactSubject
                   || messageType
                      <> Some OperationalFactEnvelope.UsageFactMessageType then
                    logger.LogWarning(
                        "Dead-lettering unsupported operational fact envelope. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Subject: {Subject}; MessageType: {MessageType}.",
                        message.MessageId,
                        message.CorrelationId,
                        message.DeliveryCount,
                        message.Subject,
                        messageType |> Option.defaultValue "<missing>"
                    )

                    do!
                        deadLetterAsync
                            "UnsupportedOperationalFactEnvelope"
                            "Message subject or graceMessageType is not supported by the operations usage worker."
                            actions
                            cancellationToken
                else
                    let usageFact = deserializeUsageFact message.Body

                    match UsageFact.Validate usageFact with
                    | Error errors ->
                        logger.LogWarning(
                            "Dead-lettering invalid UsageFact. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Errors: {ValidationErrors}.",
                            message.MessageId,
                            message.CorrelationId,
                            message.DeliveryCount,
                            describeErrors errors
                        )

                        do! deadLetterAsync "InvalidUsageFact" (describeErrors errors) actions cancellationToken
                    | Ok () ->
                        let! stored = store.StoreUsageFactAsync(usageFact, cancellationToken)

                        match stored with
                        | Error errors ->
                            logger.LogWarning(
                                "Dead-lettering UsageFact rejected by operations storage validation. UsageFactId: {UsageFactId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Errors: {ValidationErrors}.",
                                usageFact.UsageFactId,
                                usageFact.CorrelationId,
                                message.DeliveryCount,
                                describeErrors errors
                            )

                            do! deadLetterAsync "InvalidUsageFact" (describeErrors errors) actions cancellationToken
                        | Ok result ->
                            logger.LogInformation(
                                "Processed operational UsageFact. UsageFactId: {UsageFactId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Status: {Status}; BucketStart: {BucketStart}.",
                                result.UsageFactId,
                                usageFact.CorrelationId,
                                message.DeliveryCount,
                                result.Status,
                                result.Aggregate
                                |> Option.map (fun aggregate -> aggregate.Key.BucketStart)
                                |> Option.defaultValue Instant.MinValue
                            )

                            do! actions.CompleteAsync cancellationToken
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                logger.LogWarning(
                    "Operational UsageFact processing was cancelled before message settlement. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}.",
                    message.MessageId,
                    message.CorrelationId,
                    message.DeliveryCount
                )
            | :? JsonException as ex ->
                logger.LogWarning(
                    ex,
                    "Dead-lettering malformed UsageFact JSON. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}.",
                    message.MessageId,
                    message.CorrelationId,
                    message.DeliveryCount
                )

                do! deadLetterAsync "MalformedUsageFactJson" "UsageFact JSON could not be deserialized." actions cancellationToken
            | ex ->
                logger.LogError(
                    ex,
                    "Abandoning operational UsageFact message after transient processing failure. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}.",
                    message.MessageId,
                    message.CorrelationId,
                    message.DeliveryCount
                )

                do! actions.AbandonAsync cancellationToken
        }

/// Adapts Azure Service Bus message settlement to the worker's fakeable action interface.
type internal ServiceBusOperationsUsageMessageActions(args: ProcessMessageEventArgs) =

    interface IOperationsUsageMessageActions with

        member _.CompleteAsync(cancellationToken) = args.CompleteMessageAsync(args.Message, cancellationToken)

        member _.AbandonAsync(cancellationToken) = args.AbandonMessageAsync(args.Message, cancellationToken = cancellationToken)

        member _.DeadLetterAsync(reason, description, cancellationToken) =
            args.DeadLetterMessageAsync(
                args.Message,
                deadLetterReason = reason,
                deadLetterErrorDescription = description,
                cancellationToken = cancellationToken
            )

/// Converts Service Bus messages into the processor's redacted, deterministic input shape.
[<RequireQualifiedAccess>]
module internal OperationsUsageServiceBusMessage =

    /// Creates the ingestion processor message model from a received Service Bus message.
    let fromReceivedMessage (message: ServiceBusReceivedMessage) =
        {
            MessageId = message.MessageId
            CorrelationId = message.CorrelationId
            DeliveryCount = message.DeliveryCount
            Subject = message.Subject
            ApplicationProperties = message.ApplicationProperties
            Body = message.Body.ToArray()
        }

/// Runs the operational fact Service Bus processor for the Grace operations worker process.
type OperationsUsageWorkerService
    (
        settings: OperationsWorkerSettings,
        schema: OperationsUsageSchema,
        processor: OperationsUsageIngestionProcessor,
        logger: ILogger<OperationsUsageWorkerService>
    ) =
    let credential = lazy (DefaultAzureCredential() :> TokenCredential)
    let mutable serviceBusClient: ServiceBusClient option = None
    let mutable serviceBusProcessor: ServiceBusProcessor option = None

    /// Creates a Service Bus client from connection string or managed identity settings.
    let createClient () =
        match settings.ServiceBusConnectionString, settings.ServiceBusFullyQualifiedNamespace with
        | Some connectionString, _ -> ServiceBusClient(connectionString)
        | None, Some fullyQualifiedNamespace -> ServiceBusClient(fullyQualifiedNamespace, credential.Value)
        | None, None -> invalidOp "Azure Service Bus connection string or namespace must be configured."

    /// Starts the Azure Service Bus processor after SQL schema initialization succeeds.
    let startProcessingAsync (cancellationToken: CancellationToken) =
        task {
            if serviceBusProcessor.IsSome then
                logger.LogDebug("Operations usage worker already running; skipping duplicate startup.")
            else
                let mutable ready = false

                while not ready
                      && not cancellationToken.IsCancellationRequested do
                    let mutable createdClient = None
                    let mutable createdProcessor = None

                    try
                        do! schema.EnsureCreatedAsync cancellationToken

                        let client = createClient ()
                        createdClient <- Some client

                        let processorOptions =
                            ServiceBusProcessorOptions(
                                AutoCompleteMessages = false,
                                MaxConcurrentCalls = settings.MaxConcurrentCalls,
                                PrefetchCount = settings.PrefetchCount,
                                Identifier = Environment.MachineName
                            )

                        let azureProcessor = client.CreateProcessor(settings.TopicName, settings.SubscriptionName, processorOptions)

                        createdProcessor <- Some azureProcessor

                        azureProcessor.add_ProcessMessageAsync (
                            Func<ProcessMessageEventArgs, Task> (fun args ->
                                let message = OperationsUsageServiceBusMessage.fromReceivedMessage args.Message
                                let actions = ServiceBusOperationsUsageMessageActions args
                                processor.ProcessMessageAsync(message, actions, args.CancellationToken))
                        )

                        azureProcessor.add_ProcessErrorAsync (
                            Func<ProcessErrorEventArgs, Task> (fun args ->
                                logger.LogError(
                                    args.Exception,
                                    "Operations usage Service Bus processor fault. ErrorSource: {ErrorSource}; EntityPath: {EntityPath}; FullyQualifiedNamespace: {FullyQualifiedNamespace}.",
                                    args.ErrorSource,
                                    args.EntityPath,
                                    args.FullyQualifiedNamespace
                                )

                                Task.CompletedTask)
                        )

                        do! azureProcessor.StartProcessingAsync cancellationToken
                        serviceBusClient <- Some client
                        serviceBusProcessor <- Some azureProcessor
                        createdClient <- None
                        createdProcessor <- None

                        logger.LogInformation(
                            "Started operations usage worker for topic {TopicName} / subscription {SubscriptionName}.",
                            settings.TopicName,
                            settings.SubscriptionName
                        )

                        ready <- true
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> ()
                    | ex ->
                        match createdProcessor with
                        | Some processor -> do! processor.DisposeAsync()
                        | None -> ()

                        match createdClient with
                        | Some client -> do! client.DisposeAsync()
                        | None -> ()

                        logger.LogWarning(ex, "Operations usage worker dependencies are not ready; pausing for five seconds before retrying.")

                        do! Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken)
        }

    /// Stops the Azure Service Bus processor and releases its client.
    let stopProcessingAsync cancellationToken =
        task {
            match serviceBusProcessor with
            | Some processor ->
                try
                    do! processor.StopProcessingAsync cancellationToken
                with
                | ex -> logger.LogWarning(ex, "Operations usage processor stop failed; continuing with Dispose().")

                do! processor.DisposeAsync()
                serviceBusProcessor <- None
            | None -> ()

            match serviceBusClient with
            | Some client ->
                do! client.DisposeAsync()
                serviceBusClient <- None
            | None -> ()
        }

    interface IHostedService with

        /// Starts operational usage ingestion.
        member _.StartAsync(cancellationToken: CancellationToken) = startProcessingAsync cancellationToken

        /// Stops operational usage ingestion.
        member _.StopAsync(cancellationToken: CancellationToken) = stopProcessingAsync cancellationToken
