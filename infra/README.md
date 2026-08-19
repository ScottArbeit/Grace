# Grace Azure infrastructure profiles

Grace keeps its disposable infrastructure exercise separate from its future development and production topology.
Both entry points share focused resource modules, but they make different capacity choices intentionally.

## Profiles

| Profile | Cosmos DB | Azure SQL Database | Azure Managed Redis | Intended use |
| --- | --- | --- | --- | --- |
| `main.lab.bicep` | Serverless | General Purpose serverless | `Balanced_B0`, one node | Short-lived infrastructure practice |
| `main.production.bicep` | Provisioned throughput | Provisioned compute | Caller-selected SKU, two-node high availability | Development or production design input |

The production-shaped profile deliberately has no parameter file. It requires explicit Cosmos throughput, SQL SKU and
vCore capacity, SQL maximum size, and Redis SKU values. Those choices need workload analysis before real development or
production deployment. It is not a complete production environment: networking, application hosting, secrets, and
environment-specific reliability decisions remain separate work.

## Lab resources

The lab profile creates these top-level billable resources in one disposable resource group:

- Standard LRS StorageV2 account and Grace blob containers
- Cosmos DB for NoSQL serverless account, database, and `/PartitionKey` container
- Service Bus Standard namespace with Grace event and operational-usage topics and subscriptions
- Azure SQL Database General Purpose serverless database with Microsoft Entra-only administration
- Azure Managed Redis `Balanced_B0` instance without high availability

The template grants the signed-in developer the Storage blob/table, Cosmos DB data contributor, and Service Bus data
owner roles needed by Grace's `DefaultAzureCredential` development path. It does not emit resource keys or passwords.

## Cost boundary

The August 2026 retail estimate for one light West US 2 deployment, validation run, and teardown within approximately one
hour is less than USD 0.65. Use USD 1 as the conservative one-attempt budget. Azure SQL compute dominates the estimate.
The lab is not designed to remain deployed.

## Run the lab

The wrapper verifies the subscription name and ID before every operation. `WhatIf` registers the six required resource
providers and creates the otherwise free disposable resource group before running Azure validation and `what-if`.

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
