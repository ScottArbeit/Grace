namespace Grace.Shared

open System
open System.Collections.Generic

module AzureEnvironment =

    type StorageEndpoints = { BlobEndpoint: Uri; QueueEndpoint: Uri; TableEndpoint: Uri; AccountName: string; ConnectionString: string option }

    let private tryGetEnv (name: string) =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let private parseConnectionString (value: string option) =
        let dictionary = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        match value with
        | None -> dictionary
        | Some raw ->
            for segment in raw.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries) do
                let idx = segment.IndexOf('=')

                if idx > 0 then
                    let key = segment.Substring(0, idx).Trim()
                    let data = segment.Substring(idx + 1).Trim()

                    if key.Length > 0 && data.Length > 0 then dictionary[key] <- data

            dictionary

    let private tryGetValue key (dictionary: Dictionary<string, string>) =
        match dictionary.TryGetValue(key) with
        | true, value when not (String.IsNullOrWhiteSpace value) -> Some(value.Trim())
        | _ -> None

    let private storageConnectionStringValue = tryGetEnv Constants.EnvironmentVariables.AzureStorageConnectionString
    let private storageSegments = parseConnectionString storageConnectionStringValue

    let private cosmosConnectionStringValue = tryGetEnv Constants.EnvironmentVariables.AzureCosmosDBConnectionString
    let private cosmosSegments = parseConnectionString cosmosConnectionStringValue

    let private serviceBusConnectionStringValue = tryGetEnv Constants.EnvironmentVariables.AzureServiceBusConnectionString
    let private serviceBusSegments = parseConnectionString serviceBusConnectionStringValue

    let debugEnvironment = tryGetEnv Constants.EnvironmentVariables.DebugEnvironment

    let useManagedIdentity =
        match debugEnvironment with
        | Some value when value.Equals("Local", StringComparison.OrdinalIgnoreCase) -> false
        | _ -> true

    let useManagedIdentityForStorage =
        useManagedIdentity
        && storageConnectionStringValue.IsNone

    let useManagedIdentityForCosmos =
        useManagedIdentity
        && cosmosConnectionStringValue.IsNone

    let useManagedIdentityForServiceBus =
        useManagedIdentity
        && serviceBusConnectionStringValue.IsNone

    let private getStorageAccountName () =
        tryGetEnv Constants.EnvironmentVariables.AzureStorageAccountName
        |> Option.orElse (tryGetValue "AccountName" storageSegments)
        |> function
            | Some name -> name
            | None when useManagedIdentity ->
                invalidOp "Azure Storage account name must be provided via grace__azure_storage__account_name when using a managed identity."
            | None -> Constants.DefaultObjectStorageAccount

    let private getStorageEndpointSuffix () =
        tryGetEnv Constants.EnvironmentVariables.AzureStorageEndpointSuffix
        |> Option.orElse (tryGetValue "EndpointSuffix" storageSegments)
        |> Option.defaultValue "core.windows.net"

    let private ensureUri (value: string) =
        let trimmed = value.Trim().TrimEnd('/')
        Uri(trimmed, UriKind.Absolute)

    let private buildStorageUri service endpointKey =
        match tryGetValue endpointKey storageSegments with
        | Some endpoint -> ensureUri (endpoint)
        | None ->
            let accountName = getStorageAccountName ()
            let suffix = getStorageEndpointSuffix ()
            ensureUri $"https://{accountName}.{service}.{suffix}"

    let storageEndpoints =
        {
            BlobEndpoint = buildStorageUri "blob" "BlobEndpoint"
            QueueEndpoint = buildStorageUri "queue" "QueueEndpoint"
            TableEndpoint = buildStorageUri "table" "TableEndpoint"
            AccountName = getStorageAccountName ()
            ConnectionString = storageConnectionStringValue
        }

    let cosmosConnectionString = cosmosConnectionStringValue

    let tryGetCosmosEndpointUri () =
        tryGetEnv Constants.EnvironmentVariables.AzureCosmosDBEndpoint
        |> Option.map ensureUri
        |> Option.orElse (
            tryGetValue "AccountEndpoint" cosmosSegments
            |> Option.map ensureUri
        )

    let tryGetServiceBusConnectionString () = serviceBusConnectionStringValue

    let private normalizeServiceBusNamespace (value: string) =
        let trimmed = value.Trim()

        let withoutScheme =
            if trimmed.StartsWith("sb://", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(5)
            else
                trimmed

        let candidate = withoutScheme.Trim().TrimEnd('/')

        if candidate.Contains(".") then
            candidate
        else
            $"{candidate}.servicebus.windows.net"

    let tryGetServiceBusFullyQualifiedNamespace () =
        match tryGetEnv Constants.EnvironmentVariables.AzureServiceBusNamespace with
        | Some value -> Some(normalizeServiceBusNamespace value)
        | None ->
            match tryGetValue "Endpoint" serviceBusSegments with
            | Some endpoint -> Some(normalizeServiceBusNamespace endpoint)
            | None -> None
