# Grace Azure infrastructure profiles

Grace keeps its disposable infrastructure exercise separate from its future development and production topology.
Both entry points share focused resource modules, but they make different capacity choices intentionally.

## Profiles

| Profile | Cosmos DB | Azure SQL Database | Azure Managed Redis | Intended use |
| --- | --- | --- | --- | --- |
| `main.lab.bicep` | Serverless | General Purpose serverless | TLS-only ACI Redis container | Short-lived infrastructure practice |
| `main.production.bicep` | Provisioned throughput | Provisioned compute | Caller-selected SKU, two-node high availability | One production-shaped Grace Server deployment |

The production-shaped profile deliberately has no parameter file. It requires explicit Cosmos throughput, SQL SKU and
vCore capacity, SQL maximum size, and Redis SKU values. Those choices need workload analysis before real development or
production deployment. The profile also creates a Basic Azure Container Registry, one user-assigned managed identity,
a Container Apps environment, and one externally reachable Grace Server replica. The Container App uses the identity
to pull an immutable image without registry administrator credentials. Private networking and environment-specific
reliability decisions remain separate work.

## Build and deploy Grace Server

The production container listens for HTTP on port 5000. Container Apps terminates public HTTPS and forwards requests to
that port. A TCP startup probe checks that the process is listening; readiness and liveness probes call the existing
`/healthz` route without changing that route's dependency semantics. The profile fixes both minimum and maximum replicas
at one.

The first deployment is intentionally explicit because the immutable image must exist before the Container App can run.
Create the identity and registry, build the image in ACR, and then deploy the complete profile. These commands create
billable Azure resources; review the subscription, resource group, names, cost, and cleanup plan before running them.

PowerShell:

```powershell
$subscriptionId = '<subscription-id>'
$resourceGroupName = '<resource-group-name>'
$location = '<azure-region>'
$environmentName = 'development'
$deploymentSuffix = '<stable-unique-suffix>'
$identityName = "grace-server-$environmentName-$deploymentSuffix"
$registryName = "grace$($environmentName.Replace('-', ''))$($deploymentSuffix.Replace('-', ''))acr".ToLowerInvariant()

az account set --subscription $subscriptionId

az deployment group create `
    --resource-group $resourceGroupName `
    --name grace-server-identity `
    --template-file ./infra/modules/managed-identity.bicep `
    --parameters name=$identityName location=$location tags='{}'

$principalId = az identity show `
    --resource-group $resourceGroupName `
    --name $identityName `
    --query principalId `
    --output tsv

az deployment group create `
    --resource-group $resourceGroupName `
    --name grace-container-registry `
    --template-file ./infra/modules/container-registry.bicep `
    --parameters name=$registryName location=$location tags='{}' imagePullPrincipalId=$principalId

az acr build `
    --registry $registryName `
    --image grace-server:tracer `
    --file ./src/Grace.Server/Dockerfile `
    ./src

$digest = az acr repository show `
    --name $registryName `
    --image grace-server:tracer `
    --query digest `
    --output tsv
$image = "$registryName.azurecr.io/grace-server@$digest"

az deployment group create `
    --resource-group $resourceGroupName `
    --name grace-production `
    --template-file ./infra/main.production.bicep `
    --parameters `
        environmentName=$environmentName `
        deploymentSuffix=$deploymentSuffix `
        location=$location `
        developerPrincipalId='<developer-object-id>' `
        developerPrincipalName='<developer-upn>' `
        cosmosProvisionedThroughput=400 `
        sqlSkuName='<provisioned-sql-sku>' `
        sqlVCoreCapacity=1 `
        sqlMaxSizeBytes=1073741824 `
        redisSkuName='<managed-redis-sku>' `
        graceServerImage=$image

$fqdn = az deployment group show `
    --resource-group $resourceGroupName `
    --name grace-production `
    --query properties.outputs.graceServerFqdn.value `
    --output tsv

Invoke-WebRequest -Uri "https://$fqdn/healthz"
```

The complete deployment outputs the registry login server, public Grace Server hostname, deployed revision name, and
managed-identity client and principal IDs for later slices. Re-running the complete deployment with a new digest creates
a new immutable Container Apps revision. Do not pass registry passwords, administrator credentials, or access tokens as
application settings. This tracer deliberately does not activate Grace's Redis client settings; issue #983 owns the
Azure Managed Redis authentication and readiness contract.

## Lab resources

The lab profile creates these top-level billable resources in one disposable resource group:

- Standard LRS StorageV2 account and Grace blob containers
- Cosmos DB for NoSQL serverless account, database, and `/PartitionKey` container
- Service Bus Standard namespace with partitioned Grace event and operational-usage topics and subscriptions
- Azure SQL Database General Purpose serverless database with Microsoft Entra-only administration
- Public TLS-only Azure Container Instances group running `redis:7.4-alpine`

The template grants the signed-in developer the Storage blob/table, Cosmos DB data contributor, and Service Bus data
owner roles needed by Grace's `DefaultAzureCredential` development path. It does not emit resource keys or passwords.
The Redis container exposes only port 6380 on a certificate-valid public DNS name. Each runner invocation generates a
short-lived CA, server certificate, ACL username, and password, deploys them through secure Bicep parameters, starts
`DebugAzure`, and completes an authenticated TLS `PING` before clearing parent-process secrets and reporting readiness.
The production-shaped profile retains Azure Managed Redis because ACI is only a fast disposable lab substitute.

## Cost boundary

The August 2026 retail estimate for one light West US 2 deployment, validation run, and teardown within approximately one
hour is less than USD 0.65. Use USD 1 as the conservative one-attempt budget. Azure SQL compute dominates the estimate.
The lab is not designed to remain deployed.

## Run the lab

The wrapper verifies the subscription name and ID before every operation. `WhatIf` registers the six required resource
providers and creates the otherwise free disposable resource group before running Azure validation and `what-if`.
Each action prints timestamped progress messages before long-running or externally visible steps. By default, the local
date in `yyyyMMdd` form becomes the deployment suffix and the matching `rg-grace-infra-lab-<suffix>` resource group
name. Pass `-DeploymentSuffix` and `-ResourceGroupName` explicitly when operating a lab created on a different date.

PowerShell:

```powershell
$subscriptionId = '<subscription-id>'

pwsh ./scripts/Invoke-GraceInfrastructureLab.ps1 `
    -Action Build `
    -SubscriptionId $subscriptionId

pwsh ./scripts/Invoke-GraceInfrastructureLab.ps1 `
    -Action WhatIf `
    -SubscriptionId $subscriptionId
```

Review the complete `what-if` result before deployment. It must contain only the resource types listed above plus their
child resources, deployments, and role assignments.

`Deploy` also starts `DebugAzure` and leaves it running after the authenticated Redis readiness proof. Generated Redis
credentials and private keys are never written to the repository or printed by the runner. Stop any existing
`DebugAzure` process first: deployment fails before rotating credentials when `http://localhost:5000` already has a
listener, so an older child cannot satisfy readiness for the new run.

PowerShell:

```powershell
pwsh ./scripts/Invoke-GraceInfrastructureLab.ps1 `
    -Action Deploy `
    -SubscriptionId $subscriptionId

pwsh ./scripts/Invoke-GraceInfrastructureLab.ps1 `
    -Action Verify `
    -SubscriptionId $subscriptionId
```

## Tear down the lab

Removal requires an explicit switch and accepts deletion asynchronously. Verify removal in a later command so a long
Azure deletion does not hide progress or prevent interruption.

PowerShell:

```powershell
pwsh ./scripts/Invoke-GraceInfrastructureLab.ps1 `
    -Action Remove `
    -SubscriptionId $subscriptionId `
    -ConfirmRemove

pwsh ./scripts/Invoke-GraceInfrastructureLab.ps1 `
    -Action VerifyRemoved `
    -SubscriptionId $subscriptionId
```

The removal path accepts only resource group names beginning with `rg-grace-infra-lab-`, verifies the complete resource
group ID against the selected subscription, and then deletes that exact group.
