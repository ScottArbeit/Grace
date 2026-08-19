#Requires -Version 7.6

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Build', 'WhatIf', 'Deploy', 'Verify', 'Remove', 'VerifyRemoved')]
    [string] $Action,

    [Parameter(Mandatory)]
    [string] $SubscriptionId,

    [string] $ExpectedSubscriptionName = 'Grace Infrastructure Lab',

    [string] $ResourceGroupName = 'rg-grace-infra-lab-20260818',

    [string] $Location = 'westus2',

    [string] $DeploymentSuffix = '20260818',

    [string] $ClientIpAddress = '',

    [switch] $ConfirmRemove
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$labTemplatePath = Join-Path $repositoryRoot 'infra\main.lab.bicep'
$productionTemplatePath = Join-Path $repositoryRoot 'infra\main.production.bicep'
$profileAssertionPath = Join-Path $repositoryRoot 'infra\tests\Assert-InfrastructureProfiles.ps1'
$deploymentName = "grace-infra-lab-$DeploymentSuffix"
$resourceGroupPattern = '^rg-grace-infra-lab-[a-z0-9-]+$'
$normalizedSuffix = $DeploymentSuffix.ToLowerInvariant().Replace('-', '')
$storageName = "gracelab$normalizedSuffix"
$cosmosName = "grace-cosmos-lab-$normalizedSuffix"
$serviceBusName = "grace-sb-lab-$normalizedSuffix"
$sqlServerName = "grace-sql-lab-$normalizedSuffix"
$redisName = "grace-redis-lab-$normalizedSuffix"

function Invoke-AzureCli {
    <#
    .SYNOPSIS
    Runs Azure CLI and fails immediately when Azure reports an error.
    #>
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ($output -join [Environment]::NewLine)
    }

    return $output
}

function Set-VerifiedSubscription {
    <#
    .SYNOPSIS
    Selects the exact disposable subscription and verifies its identity and state.
    #>
    Invoke-AzureCli @('account', 'set', '--subscription', $SubscriptionId) | Out-Null
    $account = Invoke-AzureCli @(
        'account', 'show',
        '--query', '{id:id,name:name,state:state,tenantId:tenantId,user:user.name}',
        '--output', 'json'
    ) | ConvertFrom-Json

    if ($account.id -ne $SubscriptionId) {
        throw "Azure selected subscription '$($account.id)' instead of '$SubscriptionId'."
    }

    if ($account.name -ne $ExpectedSubscriptionName) {
        throw "Subscription '$SubscriptionId' is named '$($account.name)', not '$ExpectedSubscriptionName'."
    }

    if ($account.state -ne 'Enabled') {
        throw "Subscription '$SubscriptionId' is '$($account.state)', not Enabled."
    }

    Write-Host "Subscription: $($account.name) ($($account.id))"
    Write-Host "Tenant:       $($account.tenantId)"
    Write-Host "Signed in as: $($account.user)"
}

function Get-DeveloperIdentity {
    <#
    .SYNOPSIS
    Resolves the signed-in Microsoft Entra user used for data-plane validation and SQL administration.
    #>
    $identity = Invoke-AzureCli @(
        'ad', 'signed-in-user', 'show',
        '--query', '{id:id,userPrincipalName:userPrincipalName}',
        '--output', 'json'
    ) | ConvertFrom-Json

    if ([string]::IsNullOrWhiteSpace($identity.id) -or [string]::IsNullOrWhiteSpace($identity.userPrincipalName)) {
        throw 'Azure CLI did not return the signed-in user object ID and user principal name.'
    }

    return $identity
}

function Get-ValidatedClientIpAddress {
    <#
    .SYNOPSIS
    Resolves and validates the single public IPv4 address allowed through the disposable SQL firewall.
    #>
    if (-not [string]::IsNullOrWhiteSpace($ClientIpAddress)) {
        $candidate = $ClientIpAddress
    }
    else {
        $candidate = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
    }

    $parsedAddress = $null
    if (-not [System.Net.IPAddress]::TryParse($candidate, [ref] $parsedAddress) -or
        $parsedAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "'$candidate' is not a valid public IPv4 address."
    }

    return $candidate
}

function Invoke-BicepBuilds {
    <#
    .SYNOPSIS
    Compiles both infrastructure profiles and verifies their intentional SKU split.
    #>
    & $profileAssertionPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Infrastructure profile assertions failed.'
    }

    foreach ($templatePath in @($labTemplatePath, $productionTemplatePath)) {
        Invoke-AzureCli @('bicep', 'build', '--file', $templatePath, '--stdout') | Out-Null
        Write-Host "Built $templatePath"
    }
}

function Register-LabResourceProviders {
    <#
    .SYNOPSIS
    Registers only the Azure resource providers required by the lab template.
    #>
    $providers = @(
        'Microsoft.Authorization',
        'Microsoft.Cache',
        'Microsoft.DocumentDB',
        'Microsoft.ServiceBus',
        'Microsoft.Sql',
        'Microsoft.Storage'
    )

    foreach ($provider in $providers) {
        Write-Host "Registering $provider"
        Invoke-AzureCli @('provider', 'register', '--namespace', $provider, '--wait') | Out-Null
    }
}

function New-LabResourceGroup {
    <#
    .SYNOPSIS
    Creates the explicitly named disposable lab resource group when it does not exist.
    #>
    if ($ResourceGroupName -notmatch $resourceGroupPattern) {
        throw "Resource group '$ResourceGroupName' does not match '$resourceGroupPattern'."
    }

    Invoke-AzureCli @(
        'group', 'create',
        '--name', $ResourceGroupName,
        '--location', $Location,
        '--tags', 'environment=infrastructure-lab', 'lifecycle=disposable', 'project=Grace',
        '--output', 'none'
    ) | Out-Null
}

function Get-DeploymentArguments {
    <#
    .SYNOPSIS
    Builds the shared Azure CLI arguments for validation, what-if, and deployment.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Identity,

        [Parameter(Mandatory)]
        [string] $PublicIpAddress
    )

    return @(
        '--resource-group', $ResourceGroupName,
        '--name', $deploymentName,
        '--template-file', $labTemplatePath,
        '--parameters',
        "deploymentSuffix=$DeploymentSuffix",
        "developerPrincipalId=$($Identity.id)",
        "developerPrincipalName=$($Identity.userPrincipalName)",
        "clientIpAddress=$PublicIpAddress"
    )
}

function Test-LabResources {
    <#
    .SYNOPSIS
    Verifies the deployed lab contains exactly the expected top-level billable resource types and SKU choices.
    #>
    $resources = Invoke-AzureCli @(
        'resource', 'list',
        '--resource-group', $ResourceGroupName,
        '--query', '[].{name:name,type:type,sku:sku.name}',
        '--output', 'json'
    ) | ConvertFrom-Json

    $expectedTopLevelTypes = @(
        'Microsoft.Cache/redisEnterprise',
        'Microsoft.DocumentDB/databaseAccounts',
        'Microsoft.ServiceBus/namespaces',
        'Microsoft.Sql/servers',
        'Microsoft.Storage/storageAccounts'
    )
    $allowedTypes = @(
        $expectedTopLevelTypes
        'Microsoft.Authorization/roleAssignments'
        'Microsoft.Cache/redisEnterprise/databases'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers'
        'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments'
        'Microsoft.ServiceBus/namespaces/topics'
        'Microsoft.ServiceBus/namespaces/topics/subscriptions'
        'Microsoft.Sql/servers/databases'
        'Microsoft.Sql/servers/firewallRules'
        'Microsoft.Storage/storageAccounts/blobServices'
        'Microsoft.Storage/storageAccounts/blobServices/containers'
    )
    $actualTypes = @($resources.type | Sort-Object -Unique)
    $unexpectedTypes = @($actualTypes | Where-Object { $_ -notin $allowedTypes })
    $missingTypes = @($expectedTopLevelTypes | Where-Object { $_ -notin $actualTypes })

    if ($unexpectedTypes.Count -gt 0 -or $missingTypes.Count -gt 0) {
        throw "Lab resource types differ. Missing: $($missingTypes -join ', '); unexpected: $($unexpectedTypes -join ', ')."
    }

    $storageSku = Invoke-AzureCli @(
        'storage', 'account', 'show', '--resource-group', $ResourceGroupName, '--name', $storageName,
        '--query', 'sku.name', '--output', 'tsv'
    )
    if ($storageSku -ne 'Standard_LRS') {
        throw "Storage '$storageName' uses '$storageSku', not Standard_LRS."
    }

    $cosmosCapabilities = @(Invoke-AzureCli @(
        'cosmosdb', 'show', '--resource-group', $ResourceGroupName, '--name', $cosmosName,
        '--query', 'capabilities[].name', '--output', 'tsv'
    ))
    if ('EnableServerless' -notin $cosmosCapabilities) {
        throw "Cosmos DB '$cosmosName' is not serverless."
    }

    $serviceBusSku = Invoke-AzureCli @(
        'servicebus', 'namespace', 'show', '--resource-group', $ResourceGroupName, '--name', $serviceBusName,
        '--query', 'sku.name', '--output', 'tsv'
    )
    if ($serviceBusSku -ne 'Standard') {
        throw "Service Bus '$serviceBusName' uses '$serviceBusSku', not Standard."
    }

    $sqlConfiguration = Invoke-AzureCli @(
        'sql', 'db', 'show', '--resource-group', $ResourceGroupName, '--server', $sqlServerName,
        '--name', 'GraceOperations',
        '--query', '{requestedSku:requestedServiceObjectiveName,capacity:currentSku.capacity,autoPauseDelay:autoPauseDelay,minCapacity:minCapacity}',
        '--output', 'json'
    ) | ConvertFrom-Json
    if ($sqlConfiguration.requestedSku -ne 'GP_S_Gen5_1' -or $sqlConfiguration.capacity -ne 1 -or
        $sqlConfiguration.autoPauseDelay -ne 60 -or $sqlConfiguration.minCapacity -ne 0.5) {
        throw "SQL lab configuration differs from GP_S_Gen5_1, 60-minute auto-pause, and 0.5 minimum vCore."
    }

    $redisConfiguration = Invoke-AzureCli @(
        'resource', 'show', '--resource-group', $ResourceGroupName, '--name', $redisName,
        '--resource-type', 'Microsoft.Cache/redisEnterprise', '--api-version', '2025-04-01',
        '--query', '{sku:sku.name,highAvailability:properties.highAvailability}', '--output', 'json'
    ) | ConvertFrom-Json
    if ($redisConfiguration.sku -ne 'Balanced_B0' -or $redisConfiguration.highAvailability -ne 'Disabled') {
        throw "Redis lab configuration differs from one-node Balanced_B0."
    }

    $resources | Sort-Object type | Format-Table name, type, sku -AutoSize
    Write-Host 'Live lab resource verification passed.' -ForegroundColor Green
}

Set-VerifiedSubscription

switch ($Action) {
    'Build' {
        Invoke-BicepBuilds
    }
    'WhatIf' {
        Invoke-BicepBuilds
        Register-LabResourceProviders
        New-LabResourceGroup
        $identity = Get-DeveloperIdentity
        $publicIpAddress = Get-ValidatedClientIpAddress
        $arguments = Get-DeploymentArguments -Identity $identity -PublicIpAddress $publicIpAddress
        Invoke-AzureCli (@('deployment', 'group', 'validate') + $arguments + @('--output', 'none')) | Out-Null
        Invoke-AzureCli (@('deployment', 'group', 'what-if') + $arguments + @('--no-pretty-print'))
    }
    'Deploy' {
        Invoke-BicepBuilds
        Register-LabResourceProviders
        New-LabResourceGroup
        $identity = Get-DeveloperIdentity
        $publicIpAddress = Get-ValidatedClientIpAddress
        $arguments = Get-DeploymentArguments -Identity $identity -PublicIpAddress $publicIpAddress
        Invoke-AzureCli (@('deployment', 'group', 'create') + $arguments + @('--output', 'json'))
    }
    'Verify' {
        Test-LabResources
    }
    'Remove' {
        if (-not $ConfirmRemove) {
            throw 'Removal requires -ConfirmRemove.'
        }

        if ($ResourceGroupName -notmatch $resourceGroupPattern) {
            throw "Refusing to delete resource group '$ResourceGroupName'."
        }

        $resourceGroupId = Invoke-AzureCli @(
            'group', 'show', '--name', $ResourceGroupName, '--query', 'id', '--output', 'tsv'
        )
        $expectedResourceGroupId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"
        if ($resourceGroupId -ne $expectedResourceGroupId) {
            throw "Refusing to delete '$resourceGroupId'; expected '$expectedResourceGroupId'."
        }

        Write-Host "Deleting disposable resource group $resourceGroupId"
        Invoke-AzureCli @('group', 'delete', '--name', $ResourceGroupName, '--yes', '--no-wait') | Out-Null
        Write-Host 'Deletion accepted. Run VerifyRemoved until it reports success.'
    }
    'VerifyRemoved' {
        $exists = Invoke-AzureCli @('group', 'exists', '--name', $ResourceGroupName, '--output', 'tsv')
        if ($exists -ne 'false') {
            throw "Resource group '$ResourceGroupName' still exists or is deleting."
        }

        Write-Host "Resource group '$ResourceGroupName' is absent." -ForegroundColor Green
    }
}
