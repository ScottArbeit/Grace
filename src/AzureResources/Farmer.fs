open Farmer
open Farmer.Builders
open System

// Create ARM resources here e.g. webApp { } or storageAccount { } etc.
// See https://compositionalit.github.io/farmer/api-overview/resources/ for more details.

let tags = [
    "OwnerAlias", "scarbeit"
    "Application", "Grace"
    "environment", "development"
    "Organization", "GitHub"
]

let baseName = "gracevcsdev"
let regions = [| Location.WestUS2 |]
let storageAccountCount = 10

let generateStorageAccountNames (region: Location) = 
    List.init storageAccountCount (fun i -> i) |>
    List.map (fun i -> $"{baseName}{region.ArmValue}{i:x2}")

let storageAccountNames = generateStorageAccountNames regions.[0]

let developmentApplicationIdentity = userAssignedIdentity {
    name "gracevcsDevelopmentUser"
    add_tags tags
}

let developmentCosmosDB = cosmosDb {
    name "gracevcs-development-db"
    account_name "gracevcs-development"
    add_tags tags
    throughput 400<CosmosDb.RU>
    failover_policy CosmosDb.FailoverPolicy.NoFailover
    consistency_policy (CosmosDb.ConsistencyPolicy.Session)
    add_containers [
        cosmosContainer {
            name "grace-development"
            partition_key [ "/partitionKey" ] CosmosDb.IndexKind.Hash
            //add_index "/path" [ CosmosDb.Number, CosmosDb.Hash ]
            //exclude_path "/excluded/*"
        }
    ]
}

let newStorageAccount (storageAccountName: String) = storageAccount {
    name storageAccountName
    sku Storage.Sku.Standard_LRS
    add_tags tags
}

let developmentStorage = storageAccount {
    name "gracevcsdevelopment"
    sku Storage.Sku.Standard_LRS
    add_tags tags
    //add_public_container "mypubliccontainer"
    //add_private_container "myprivatecontainer"
    //add_blob_container "myblobcontainer"
}

let storageAccount00 = newStorageAccount storageAccountNames.[0]
let storageAccount01 = newStorageAccount storageAccountNames.[1]
let storageAccount02 = newStorageAccount storageAccountNames.[2]
let storageAccount03 = newStorageAccount storageAccountNames.[3]
let storageAccount04 = newStorageAccount storageAccountNames.[4]
let storageAccount05 = newStorageAccount storageAccountNames.[5]
let storageAccount06 = newStorageAccount storageAccountNames.[6]
let storageAccount07 = newStorageAccount storageAccountNames.[7]
let storageAccount08 = newStorageAccount storageAccountNames.[8]
let storageAccount09 = newStorageAccount storageAccountNames.[9]

let developmentServiceBus = serviceBus {
    name "gracevcs-development"
    sku ServiceBus.Sku.Standard
    add_tags tags
    //add_queues [
    //    queue { name "queueA" }
    //    queue { name "queueB" }
    //]
    //add_topics [
    //    topic { name "topicA" }
    //    topic { name "topicB" }
    //]
}

let createStorageAccounts (region: Location) =
    List.init storageAccountCount (fun i -> i) |>
    List.map (fun i -> 
        newStorageAccount $"{baseName}{region.ArmValue}{i:x2}"
    )
    
// Add resources to the ARM deployment using the add_resource keyword.
// See https://compositionalit.github.io/farmer/api-overview/resources/arm/ for more details.
let developmentDeployment region = arm {
    location region
    add_resource developmentApplicationIdentity
    add_resource developmentCosmosDB
    add_resource developmentServiceBus
    add_resource developmentStorage

    add_resource storageAccount00
    add_resource storageAccount01
    add_resource storageAccount02
    add_resource storageAccount03
    add_resource storageAccount04
    add_resource storageAccount05
    add_resource storageAccount06
    add_resource storageAccount07
    add_resource storageAccount08
    add_resource storageAccount09
    
    output "azureStoragePrimaryConnectionString" developmentStorage.Key
    output ($"{storageAccountNames.[0]}PrimaryConnectionString") storageAccount00.Key
    output ($"{storageAccountNames.[1]}PrimaryConnectionString") storageAccount01.Key
    output ($"{storageAccountNames.[2]}PrimaryConnectionString") storageAccount02.Key
    output ($"{storageAccountNames.[3]}PrimaryConnectionString") storageAccount03.Key
    output ($"{storageAccountNames.[4]}PrimaryConnectionString") storageAccount04.Key
    output ($"{storageAccountNames.[5]}PrimaryConnectionString") storageAccount05.Key
    output ($"{storageAccountNames.[6]}PrimaryConnectionString") storageAccount06.Key
    output ($"{storageAccountNames.[7]}PrimaryConnectionString") storageAccount07.Key
    output ($"{storageAccountNames.[8]}PrimaryConnectionString") storageAccount08.Key
    output ($"{storageAccountNames.[9]}PrimaryConnectionString") storageAccount09.Key

    output "azureCosmosDBPrimaryConnectionString" developmentCosmosDB.PrimaryConnectionString
    output "azureCosmosDBPrimaryKey" developmentCosmosDB.PrimaryKey
    output "azureCosmosDBPrimaryReadOnlyKey" developmentCosmosDB.PrimaryReadonlyKey
    output "azureServiceBusNamespaceDefaultConnectionString" developmentServiceBus.NamespaceDefaultConnectionString
}

let extractStorageKey (connectionString: string) = 
    let startOfAccountKey = connectionString.IndexOf("AccountKey=") + "AccountKey=".Length
    connectionString.Substring(startOfAccountKey).Replace(";", "")

let createSecretWithActivationDate (secretName: string) (secretValue: string) = secret {
    name secretName
    value (ArmExpression.create $"'{secretValue}'")
    activation_date (DateTime.Now)
    enable_secret
    add_tags tags
}

let accessPolicyScott = accessPolicy {
    object_id "eb5d561c-4eca-4cfa-96b9-6af83c77c7d7"
    certificate_permissions KeyVault.Certificate.All
    secret_permissions KeyVault.Secret.All
    key_permissions KeyVault.Key.All
}

// let accessPolicyApp (developmentApplicationIdentity: UserAssignedIdentityConfig) = accessPolicy {
//     printfn "ApplicationId: %s" developmentApplicationIdentity.ClientId.Value
//     application_id (Guid.Parse(developmentApplicationIdentity.ClientId.Value))
//     secret_permissions KeyVault.Secret.ReadSecrets
// }

let addKeyVaultSecrets (outputs: Map<string, string>) = keyVault {
    name "gracevcs-development"
    sku KeyVault.Sku.Standard
    enable_soft_delete
    add_access_policy accessPolicyScott
    //add_access_policy (accessPolicyApp developmentApplicationIdentity)
    
    add_secret (createSecretWithActivationDate "AzureStorageConnectionString" outputs.["azureStoragePrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[0]}ConnectionString" outputs.[$"{storageAccountNames.[0]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[1]}ConnectionString" outputs.[$"{storageAccountNames.[1]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[2]}ConnectionString" outputs.[$"{storageAccountNames.[2]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[3]}ConnectionString" outputs.[$"{storageAccountNames.[3]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[4]}ConnectionString" outputs.[$"{storageAccountNames.[4]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[5]}ConnectionString" outputs.[$"{storageAccountNames.[5]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[6]}ConnectionString" outputs.[$"{storageAccountNames.[6]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[7]}ConnectionString" outputs.[$"{storageAccountNames.[7]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[8]}ConnectionString" outputs.[$"{storageAccountNames.[8]}PrimaryConnectionString"])
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[9]}ConnectionString" outputs.[$"{storageAccountNames.[9]}PrimaryConnectionString"])
    
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[0]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[0]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[1]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[1]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[2]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[2]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[3]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[3]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[4]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[4]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[5]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[5]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[6]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[6]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[7]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[7]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[8]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[8]}PrimaryConnectionString"]))
    add_secret (createSecretWithActivationDate $"{storageAccountNames.[9]}AccountKey" (extractStorageKey outputs.[$"{storageAccountNames.[9]}PrimaryConnectionString"]))

    // add_secret (createSecretWithActivationDate "AzureStorageKey" (extractStorageKey outputs.["azureStoragePrimaryConnectionString"]))
    // add_secret (createSecretWithActivationDate "AzureCosmosDBConnectionString" outputs.["azureCosmosDBPrimaryConnectionString"])
    // add_secret (createSecretWithActivationDate "AzureCosmosDBKey" outputs.["azureCosmosDBPrimaryKey"])
    // add_secret (createSecretWithActivationDate "AzureCosmosDBReadOnlyKey" outputs.["azureCosmosDBPrimaryReadOnlyKey"])
    // add_secret (createSecretWithActivationDate "AzureServiceBusConnectionString" outputs.["azureServiceBusNamespaceDefaultConnectionString"])
}

let keyVaultDeployment outputs = arm {
    location Location.WestUS2
    add_resource (addKeyVaultSecrets outputs)

    //output "blah" addKeyVaultSecrets.
}

regions 
|> Seq.iter (fun region ->
    developmentDeployment region |> Writer.quickWrite "MainTemplate"
    printfn "Main template written to MainTemplate.json."

    let outputs = developmentDeployment region |> Deploy.execute "gracevcs-development" Deploy.NoParameters
    printfn $"Done deploying main template.{Environment.NewLine}"

    keyVaultDeployment outputs |> Writer.quickWrite "keyVault"
    printfn "Deploying Key Vault template."
    let keyVaultOutputs = keyVaultDeployment outputs |> Deploy.execute "gracevcs-development" Deploy.NoParameters
    printfn "Done deploying Key Vault template."
    //printfn "%A" outputs
)
