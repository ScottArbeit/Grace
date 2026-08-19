#Requires -Version 7.6

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$infraRoot = Split-Path -Parent $PSScriptRoot
$labTemplatePath = Join-Path $infraRoot 'main.lab.bicep'
$productionTemplatePath = Join-Path $infraRoot 'main.production.bicep'
$serviceBusModulePath = Join-Path $infraRoot 'modules\service-bus.bicep'
$redisContainerModulePath = Join-Path $infraRoot 'modules\redis-container.bicep'
$labRunnerPath = Join-Path (Split-Path -Parent $infraRoot) 'scripts\Invoke-GraceInfrastructureLab.ps1'
$startDebugAzurePath = Join-Path (Split-Path -Parent $infraRoot) 'scripts\start-debugazure.ps1'
$inventoryAssertionsPath = Join-Path (Split-Path -Parent $infraRoot) 'scripts\GraceInfrastructureLab.Inventory.ps1'
. $inventoryAssertionsPath

function Assert-Pattern {
    <#
    .SYNOPSIS
    Verifies that a Bicep profile contains a required infrastructure choice.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $Text,

        [Parameter(Mandatory)]
        [string] $Pattern,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-PatternAbsent {
    <#
    .SYNOPSIS
    Verifies that a Bicep profile excludes a forbidden infrastructure choice.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $Text,

        [Parameter(Mandatory)]
        [string] $Pattern,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if ($Text -match $Pattern) {
        throw $Message
    }
}

$labTemplate = Get-Content -LiteralPath $labTemplatePath -Raw
$productionTemplate = Get-Content -LiteralPath $productionTemplatePath -Raw
$serviceBusModule = Get-Content -LiteralPath $serviceBusModulePath -Raw
$redisContainerModule = Get-Content -LiteralPath $redisContainerModulePath -Raw
$labRunner = Get-Content -LiteralPath $labRunnerPath -Raw

Assert-Pattern $labTemplate 'serverless:\s*true' 'The lab profile must use serverless Cosmos and SQL modules.'
Assert-Pattern $labTemplate "skuName:\s*'GP_S_Gen5_1'" 'The lab profile must use the agreed SQL serverless SKU.'
Assert-Pattern $labTemplate "modules/redis-container\.bicep" 'The lab profile must use the disposable Redis container module.'
Assert-PatternAbsent $labTemplate "modules/redis\.bicep" 'The lab profile must not deploy Azure Managed Redis.'

Assert-Pattern $productionTemplate 'serverless:\s*false' 'The production-shaped profile must use provisioned Cosmos and SQL modules.'
Assert-Pattern $productionTemplate 'param\s+cosmosProvisionedThroughput\s+int(\s|$)' 'The production-shaped profile must require Cosmos throughput.'
Assert-Pattern $productionTemplate 'param\s+sqlSkuName\s+string(\s|$)' 'The production-shaped profile must require a SQL SKU.'
Assert-Pattern $productionTemplate 'validatedSqlSkuName' 'The production-shaped profile must reject SQL serverless SKU names.'
Assert-Pattern $productionTemplate 'param\s+redisSkuName\s+string(\s|$)' 'The production-shaped profile must require a Redis SKU.'
Assert-Pattern $productionTemplate 'highAvailability:\s*true' 'The production-shaped profile must enable Redis high availability.'
Assert-PatternAbsent $productionTemplate "skuName:\s*'GP_S_" 'The production-shaped profile must not embed a SQL serverless SKU.'

Assert-Pattern $redisContainerModule 'Microsoft\.ContainerInstance/containerGroups' 'The lab Redis module must deploy Azure Container Instances.'
Assert-Pattern $redisContainerModule "image string = 'redis:7\.4-alpine'" 'The lab Redis module must pin its disposable Redis image tag.'
Assert-Pattern $redisContainerModule "--port'[\s\S]*'0'" 'The lab Redis container must disable its plaintext listener.'
Assert-Pattern $redisContainerModule "--tls-port'[\s\S]*'6380'" 'The lab Redis container must listen on the accepted TLS port.'
Assert-Pattern $redisContainerModule "--aclfile'" 'The lab Redis container must load its generated ACL from a mounted secret.'
Assert-Pattern $redisContainerModule "type:\s*'Public'" 'The lab Redis container must expose its certificate hostname publicly.'
Assert-Pattern $redisContainerModule 'ports:\s*\[[\s\S]*port:\s*6380' 'The lab Redis container must expose TLS port 6380.'
Assert-PatternAbsent $redisContainerModule 'port:\s*6379' 'The lab Redis container must not expose plaintext port 6379.'
Assert-Pattern $redisContainerModule '@secure\(\)[\s\S]*param caCertificate' 'The lab Redis CA input must be a secure Bicep parameter.'
Assert-Pattern $redisContainerModule '@secure\(\)[\s\S]*param serverPrivateKey' 'The lab Redis private key input must be a secure Bicep parameter.'
Assert-Pattern $redisContainerModule '@secure\(\)[\s\S]*param aclFile' 'The lab Redis ACL input must be a secure Bicep parameter.'
Assert-PatternAbsent $redisContainerModule 'output\s+\w*(password|privateKey|acl|certificate)' 'The lab Redis module must not output generated secure material.'

Assert-Pattern $serviceBusModule 'graceUsageTopicName' 'The Service Bus module must use GraceUsage vocabulary for its usage topic.'
Assert-Pattern $serviceBusModule 'graceUsageSubscriptionName' 'The Service Bus module must use GraceUsage vocabulary for its usage subscription.'
Assert-PatternAbsent $serviceBusModule 'operationalFacts|OperationalFacts|operational-facts' 'The Service Bus template must not retain OperationalFacts vocabulary.'
Assert-Pattern $labTemplate 'serviceBusGraceUsageTopic' 'The lab profile must expose the Grace usage topic output.'
Assert-Pattern $productionTemplate 'serviceBusGraceUsageTopic' 'The production-shaped profile must expose the Grace usage topic output.'

$partitionedTopicCount = ([regex]::Matches($serviceBusModule, 'enablePartitioning:\s*true')).Count
if ($partitionedTopicCount -ne 2) {
    throw "Both Service Bus topics must enable partitioning; found $partitionedTopicCount partitioned topic declarations."
}
Assert-PatternAbsent $serviceBusModule 'enablePartitioning:\s*false' 'The Service Bus module must not create an unpartitioned topic.'
Assert-Pattern $labRunner "DeploymentSuffix\s*=\s*\(Get-Date\s+-Format\s+'yyyyMMdd'\)" 'The lab runner must derive its default deployment suffix at runtime.'
Assert-PatternAbsent $labRunner "DeploymentSuffix\s*=\s*'\d{8}'" 'The lab runner must not embed a dated deployment suffix.'
Assert-PatternAbsent $labRunner "ResourceGroupName\s*=\s*'rg-grace-infra-lab-\d{8}'" 'The lab runner must not embed a dated resource group name.'
Assert-Pattern $labRunner "readEnvironmentVariable\('GRACE_LAB_REDIS_SERVER_PRIVATE_KEY'\)" 'The runner must resolve the Redis private key through inherited environment.'
Assert-Pattern $labRunner 'Test-RedisTlsReadiness\s+-Material' 'The runner must complete authenticated TLS PING before readiness.'
Assert-Pattern $labRunner 'Test-RedisTlsReadiness[\s\S]*Clear-LabRedisSecrets[\s\S]*readiness passed' 'The runner must clear parent Redis secrets before reporting readiness.'
Assert-Pattern $labRunner '''grace__azure_storage__account_name''\s*=\s*\$outputs\.storageAccountName\.value' 'The runner must override the exact Storage setting consumed by DebugAzure.'
Assert-Pattern $labRunner '''grace__azurecosmosdb__endpoint''\s*=\s*\$outputs\.cosmosEndpoint\.value' 'The runner must override the exact Cosmos endpoint setting consumed by DebugAzure.'
Assert-Pattern $labRunner "'deployment', 'group', 'create'[\s\S]*Clear-LabRedisDeploymentSecrets[\s\S]*Deployment completed; launching DebugAzure" 'The runner must clear deployment-only Redis material after deployment and before launching DebugAzure.'
Assert-Pattern $labRunner "'deployment', 'group', 'create'[\s\S]*Clear-LabRedisDeploymentSecrets[\s\S]*& pwsh -NoProfile -File" 'The DebugAzure child must be launched only after deployment-only Redis secrets are cleared.'
Assert-Pattern $labRunner "-PreflightOnly[\s\S]*Invoke-BicepBuilds[\s\S]*'deployment', 'group', 'create'" 'Deploy must reject a pre-existing Grace Server listener before rotating Azure credentials.'
Assert-PatternAbsent $labRunner 'redis(ServerPrivateKey|AclFile|Password)=\$' 'The runner must not place secure Redis values in Azure CLI arguments.'
Assert-PatternAbsent $labRunner 'Write-(Host|LabStatus)[^\r\n]*(Password|ServerKeyBase64|AclBase64|CaBase64)' 'The runner must not write generated Redis material to status output.'

$expectedInventory = @(
    [pscustomobject]@{ name = 'expected-redis'; type = 'Microsoft.ContainerInstance/containerGroups' }
    [pscustomobject]@{ name = 'expected-storage'; type = 'Microsoft.Storage/storageAccounts' }
)
$validInventory = @(
    $expectedInventory
    [pscustomobject]@{ name = 'expected-storage/default'; type = 'Microsoft.Storage/storageAccounts/blobServices' }
)
$allowedInventoryTypes = @(
    'Microsoft.ContainerInstance/containerGroups'
    'Microsoft.Storage/storageAccounts'
    'Microsoft.Storage/storageAccounts/blobServices'
)

Assert-ExactLabResourceInventory `
    -Resources $validInventory `
    -ExpectedTopLevelResources $expectedInventory `
    -AllowedResourceTypes $allowedInventoryTypes

$inventoryWithStaleAllowedResource = @(
    $validInventory
    [pscustomobject]@{ name = 'stale-storage'; type = 'Microsoft.Storage/storageAccounts' }
)

try {
    Assert-ExactLabResourceInventory `
        -Resources $inventoryWithStaleAllowedResource `
        -ExpectedTopLevelResources $expectedInventory `
        -AllowedResourceTypes $allowedInventoryTypes
    throw 'Exact inventory assertion accepted an additional Storage account.'
}
catch {
    if ($_.Exception.Message -notmatch 'unexpected top-level: stale-storage') {
        throw
    }
}

$occupiedListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$occupiedListener.Start()
$occupiedPort = ([Net.IPEndPoint] $occupiedListener.LocalEndpoint).Port
$occupiedUri = "http://127.0.0.1:$occupiedPort"
try {
    $occupiedOutput = @(& pwsh -NoProfile -File $startDebugAzurePath -GraceServerUri $occupiedUri -PreflightOnly 2>&1)
    if ($LASTEXITCODE -eq 0) {
        throw 'DebugAzure preflight accepted an occupied Grace Server URI.'
    }

    if (($occupiedOutput -join [Environment]::NewLine) -notmatch 'already has a listener') {
        throw 'DebugAzure preflight did not explain that the configured URI was already occupied.'
    }
}
finally {
    $occupiedListener.Stop()
}

$availableOutput = @(& pwsh -NoProfile -File $startDebugAzurePath -GraceServerUri $occupiedUri -PreflightOnly 2>&1)
if ($LASTEXITCODE -ne 0 -or ($availableOutput -join [Environment]::NewLine) -notmatch 'is available for a new DebugAzure child') {
    throw 'DebugAzure preflight rejected an available Grace Server URI.'
}

Write-Host 'Infrastructure profile assertions passed.' -ForegroundColor Green
