#Requires -Version 7.6

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Build', 'WhatIf', 'Deploy', 'Verify', 'Remove', 'VerifyRemoved')]
    [string] $Action,

    [Parameter(Mandatory)]
    [string] $SubscriptionId,

    [string] $ExpectedSubscriptionName = 'Grace Infrastructure Lab',

    [string] $ResourceGroupName = '',

    [string] $Location = 'westus2',

    [string] $DeploymentSuffix = (Get-Date -Format 'yyyyMMdd'),

    [string] $ClientIpAddress = '',

    [switch] $ConfirmRemove
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ResourceGroupName)) {
    $ResourceGroupName = "rg-grace-infra-lab-$DeploymentSuffix"
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$labTemplatePath = Join-Path $repositoryRoot 'infra\main.lab.bicep'
$productionTemplatePath = Join-Path $repositoryRoot 'infra\main.production.bicep'
$profileAssertionPath = Join-Path $repositoryRoot 'infra\tests\Assert-InfrastructureProfiles.ps1'
. (Join-Path $PSScriptRoot 'GraceInfrastructureLab.Inventory.ps1')
$deploymentName = "grace-infra-lab-$DeploymentSuffix"
$resourceGroupPattern = '^rg-grace-infra-lab-[a-z0-9-]+$'
$normalizedSuffix = $DeploymentSuffix.ToLowerInvariant().Replace('-', '')
$storageName = "gracelab$normalizedSuffix"
$cosmosName = "grace-cosmos-lab-$normalizedSuffix"
$serviceBusName = "grace-sb-lab-$normalizedSuffix"
$sqlServerName = "grace-sql-lab-$normalizedSuffix"
$redisContainerGroupName = "grace-redis-lab-$normalizedSuffix"
$redisDnsName = "$redisContainerGroupName.$($Location.ToLowerInvariant()).azurecontainer.io"
$redisTlsPort = 6380
$redisParameterEnvironmentVariables = @(
    'GRACE_LAB_REDIS_CA_CERTIFICATE',
    'GRACE_LAB_REDIS_SERVER_CERTIFICATE',
    'GRACE_LAB_REDIS_SERVER_PRIVATE_KEY',
    'GRACE_LAB_REDIS_ACL_FILE'
)

function Write-LabStatus {
    <#
    .SYNOPSIS
    Writes a timestamped progress message for a lab lifecycle step.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $Message
    )

    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor Cyan
}

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

function New-LabRedisMaterial {
    <#
    .SYNOPSIS
    Creates one process-scoped CA, hostname certificate, ACL identity, and strong Redis password.
    #>
    $now = [DateTimeOffset]::UtcNow
    $username = 'grace-lab'
    $passwordBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $password = [Convert]::ToBase64String($passwordBytes)
    [Array]::Clear($passwordBytes)

    $caKey = [Security.Cryptography.RSA]::Create(3072)
    $serverKey = [Security.Cryptography.RSA]::Create(3072)
    $caCertificate = $null
    $serverCertificate = $null
    try {
        $caRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Grace Infrastructure Lab Redis CA',
            $caKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $caRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $caRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,
                $true))
        $caCertificate = $caRequest.CreateSelfSigned($now.AddMinutes(-5), $now.AddDays(2))

        $serverRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$redisDnsName",
            $serverKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $subjectNames = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $subjectNames.AddDnsName($redisDnsName)
        $serverRequest.CertificateExtensions.Add($subjectNames.Build())
        $serverRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $serverRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true))
        $serverAuthenticationOids = [Security.Cryptography.OidCollection]::new()
        $serverAuthenticationOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null
        $serverRequest.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $serverAuthenticationOids,
                $true))
        $serialNumber = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        $serverCertificate = $serverRequest.Create($caCertificate, $now.AddMinutes(-5), $now.AddDays(1), $serialNumber)
        [Array]::Clear($serialNumber)

        $caPem = $caCertificate.ExportCertificatePem()
        $serverPem = $serverCertificate.ExportCertificatePem()
        $serverKeyPem = $serverKey.ExportPkcs8PrivateKeyPem()
        $acl = "user default off`nuser $username on >$password ~* +@all`n"

        return [pscustomobject]@{
            Username = $username
            Password = $password
            CaPem = $caPem
            CaBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($caPem))
            ServerBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($serverPem))
            ServerKeyBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($serverKeyPem))
            AclBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($acl))
        }
    }
    finally {
        if ($null -ne $caCertificate) { $caCertificate.Dispose() }
        if ($null -ne $serverCertificate) { $serverCertificate.Dispose() }
        $caKey.Dispose()
        $serverKey.Dispose()
    }
}

function New-SecureRedisParameterFile {
    <#
    .SYNOPSIS
    Creates a non-secret Bicep parameter shim that reads secure values from the runner process environment.
    #>
    param(
        [Parameter(Mandatory)][pscustomobject] $Identity,
        [Parameter(Mandatory)][string] $PublicIpAddress
    )

    $parameterPath = Join-Path (Split-Path -Parent $labTemplatePath) ".grace-lab-$PID.bicepparam"
    $escapedPrincipalName = $Identity.userPrincipalName.Replace("'", "''")
    $content = @"
using './main.lab.bicep'

param deploymentSuffix = '$DeploymentSuffix'
param location = '$Location'
param developerPrincipalId = '$($Identity.id)'
param developerPrincipalName = '$escapedPrincipalName'
param clientIpAddress = '$PublicIpAddress'
param redisCaCertificate = readEnvironmentVariable('GRACE_LAB_REDIS_CA_CERTIFICATE')
param redisServerCertificate = readEnvironmentVariable('GRACE_LAB_REDIS_SERVER_CERTIFICATE')
param redisServerPrivateKey = readEnvironmentVariable('GRACE_LAB_REDIS_SERVER_PRIVATE_KEY')
param redisAclFile = readEnvironmentVariable('GRACE_LAB_REDIS_ACL_FILE')
"@
    [IO.File]::WriteAllText($parameterPath, $content, [Text.UTF8Encoding]::new($false))
    return $parameterPath
}

function Set-LabRedisDeploymentEnvironment {
    <#
    .SYNOPSIS
    Places the generated secure deployment values in inherited process environment variables.
    #>
    param([Parameter(Mandatory)][pscustomobject] $Material)

    [Environment]::SetEnvironmentVariable($redisParameterEnvironmentVariables[0], $Material.CaBase64, 'Process')
    [Environment]::SetEnvironmentVariable($redisParameterEnvironmentVariables[1], $Material.ServerBase64, 'Process')
    [Environment]::SetEnvironmentVariable($redisParameterEnvironmentVariables[2], $Material.ServerKeyBase64, 'Process')
    [Environment]::SetEnvironmentVariable($redisParameterEnvironmentVariables[3], $Material.AclBase64, 'Process')
}

function Clear-LabRedisSecrets {
    <#
    .SYNOPSIS
    Removes generated Redis deployment and client secrets from the runner process.
    #>
    foreach ($name in $redisParameterEnvironmentVariables + @(
        'grace__redis__host', 'grace__redis__port', 'grace__redis__tls',
        'grace__redis__username', 'grace__redis__password', 'grace__redis__ca_certificate')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}

function Test-RedisTlsReadiness {
    <#
    .SYNOPSIS
    Proves TLS hostname and custom-root validation, ACL authentication, and PING against the deployed endpoint.
    #>
    param([Parameter(Mandatory)][pscustomobject] $Material)

    $trustedRoot = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($Material.CaPem)
    $tcpClient = [Net.Sockets.TcpClient]::new()
    try {
        $tcpClient.ReceiveTimeout = 30000
        $tcpClient.SendTimeout = 30000
        $tcpClient.ConnectAsync($redisDnsName, $redisTlsPort).WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $validation = [Net.Security.RemoteCertificateValidationCallback] {
            param($sender, $certificate, $chain, $policyErrors)

            if ($null -eq $certificate -or
                $policyErrors.HasFlag([Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch)) {
                return $false
            }

            $serverCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)
            $customChain = [Security.Cryptography.X509Certificates.X509Chain]::new()
            try {
                $customChain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
                $customChain.ChainPolicy.CustomTrustStore.Add($trustedRoot)
                $customChain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                return $customChain.Build($serverCertificate)
            }
            finally {
                $customChain.Dispose()
                $serverCertificate.Dispose()
            }
        }

        $tlsStream = [Net.Security.SslStream]::new($tcpClient.GetStream(), $false, $validation)
        try {
            $tlsStream.ReadTimeout = 30000
            $tlsStream.WriteTimeout = 30000
            $options = [Net.Security.SslClientAuthenticationOptions]::new()
            $options.TargetHost = $redisDnsName
            $options.EnabledSslProtocols = [Security.Authentication.SslProtocols]::Tls12 -bor [Security.Authentication.SslProtocols]::Tls13
            $tlsStream.AuthenticateAsClient($options)

            $writer = [IO.StreamWriter]::new($tlsStream, [Text.UTF8Encoding]::new($false), 1024, $true)
            $reader = [IO.StreamReader]::new($tlsStream, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
            try {
                $writer.NewLine = "`r`n"
                $writer.Write("*3`r`n`$4`r`nAUTH`r`n`$$($Material.Username.Length)`r`n$($Material.Username)`r`n`$$($Material.Password.Length)`r`n$($Material.Password)`r`n")
                $writer.Write("*1`r`n`$4`r`nPING`r`n")
                $writer.Flush()

                if ($reader.ReadLine() -ne '+OK' -or $reader.ReadLine() -ne '+PONG') {
                    throw 'Redis did not accept the generated ACL credential and authenticated PING.'
                }
            }
            finally {
                $reader.Dispose()
                $writer.Dispose()
            }
        }
        finally {
            $tlsStream.Dispose()
        }
    }
    finally {
        $tcpClient.Dispose()
        $trustedRoot.Dispose()
    }
}

function Set-VerifiedSubscription {
    <#
    .SYNOPSIS
    Selects the exact disposable subscription and verifies its identity and state.
    #>
    Write-LabStatus "Selecting Azure subscription '$SubscriptionId'."
    Invoke-AzureCli @('account', 'set', '--subscription', $SubscriptionId) | Out-Null
    Write-LabStatus 'Reading the selected subscription identity and state.'
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

    Write-LabStatus 'Subscription identity verified.'
    Write-Host "Subscription: $($account.name) ($($account.id))"
    Write-Host "Tenant:       $($account.tenantId)"
    Write-Host "Signed in as: $($account.user)"
}

function Get-DeveloperIdentity {
    <#
    .SYNOPSIS
    Resolves the signed-in Microsoft Entra user used for data-plane validation and SQL administration.
    #>
    Write-LabStatus 'Resolving the signed-in Microsoft Entra user.'
    $identity = Invoke-AzureCli @(
        'ad', 'signed-in-user', 'show',
        '--query', '{id:id,userPrincipalName:userPrincipalName}',
        '--output', 'json'
    ) | ConvertFrom-Json

    if ([string]::IsNullOrWhiteSpace($identity.id) -or [string]::IsNullOrWhiteSpace($identity.userPrincipalName)) {
        throw 'Azure CLI did not return the signed-in user object ID and user principal name.'
    }

    Write-LabStatus "Developer identity resolved as '$($identity.userPrincipalName)'."
    return $identity
}

function Get-ValidatedClientIpAddress {
    <#
    .SYNOPSIS
    Resolves and validates the single public IPv4 address allowed through the disposable SQL firewall.
    #>
    if (-not [string]::IsNullOrWhiteSpace($ClientIpAddress)) {
        Write-LabStatus 'Using the caller-supplied public IPv4 address for the SQL firewall rule.'
        $candidate = $ClientIpAddress
    }
    else {
        Write-LabStatus 'Discovering the current public IPv4 address for the SQL firewall rule.'
        $candidate = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
    }

    $parsedAddress = $null
    if (-not [System.Net.IPAddress]::TryParse($candidate, [ref] $parsedAddress) -or
        $parsedAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "'$candidate' is not a valid public IPv4 address."
    }

    Write-LabStatus "Validated public IPv4 address '$candidate'."
    return $candidate
}

function Invoke-BicepBuilds {
    <#
    .SYNOPSIS
    Compiles both infrastructure profiles and verifies their intentional SKU split.
    #>
    Write-LabStatus 'Checking the lab and production-shaped profile invariants.'
    & $profileAssertionPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Infrastructure profile assertions failed.'
    }

    foreach ($templatePath in @($labTemplatePath, $productionTemplatePath)) {
        Write-LabStatus "Compiling '$templatePath'."
        Invoke-AzureCli @('bicep', 'build', '--file', $templatePath, '--stdout') | Out-Null
        Write-LabStatus "Compiled '$templatePath'."
    }
}

function Register-LabResourceProviders {
    <#
    .SYNOPSIS
    Registers only the Azure resource providers required by the lab template.
    #>
    $providers = @(
        'Microsoft.Authorization',
        'Microsoft.ContainerInstance',
        'Microsoft.DocumentDB',
        'Microsoft.ServiceBus',
        'Microsoft.Sql',
        'Microsoft.Storage'
    )

    foreach ($provider in $providers) {
        Write-LabStatus "Ensuring resource provider '$provider' is registered."
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

    Write-LabStatus "Creating or updating disposable resource group '$ResourceGroupName' in '$Location'."
    Invoke-AzureCli @(
        'group', 'create',
        '--name', $ResourceGroupName,
        '--location', $Location,
        '--tags', 'environment=infrastructure-lab', 'lifecycle=disposable', 'project=Grace',
        '--output', 'none'
    ) | Out-Null
    Write-LabStatus "Resource group '$ResourceGroupName' is ready."
}

function Get-DeploymentArguments {
    <#
    .SYNOPSIS
    Builds the shared Azure CLI arguments for validation, what-if, and deployment.
    #>
    param(
        [Parameter(Mandatory)]
        [string] $SecureParameterPath
    )

    Write-LabStatus "Preparing deployment '$deploymentName' with suffix '$DeploymentSuffix'."
    return @(
        '--resource-group', $ResourceGroupName,
        '--name', $deploymentName,
        '--parameters', $SecureParameterPath
    )
}

function Test-LabResources {
    <#
    .SYNOPSIS
    Verifies the deployed lab contains exactly the expected top-level billable resource types and SKU choices.
    #>
    Write-LabStatus "Reading deployed resources from '$ResourceGroupName'."
    $resources = Invoke-AzureCli @(
        'resource', 'list',
        '--resource-group', $ResourceGroupName,
        '--query', '[].{name:name,type:type,sku:sku.name}',
        '--output', 'json'
    ) | ConvertFrom-Json

    $expectedTopLevelResources = @(
        [pscustomobject]@{ name = $redisContainerGroupName; type = 'Microsoft.ContainerInstance/containerGroups' }
        [pscustomobject]@{ name = $cosmosName; type = 'Microsoft.DocumentDB/databaseAccounts' }
        [pscustomobject]@{ name = $serviceBusName; type = 'Microsoft.ServiceBus/namespaces' }
        [pscustomobject]@{ name = $sqlServerName; type = 'Microsoft.Sql/servers' }
        [pscustomobject]@{ name = $storageName; type = 'Microsoft.Storage/storageAccounts' }
    )
    $allowedTypes = @(
        $expectedTopLevelResources.type
        'Microsoft.Authorization/roleAssignments'
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
    Assert-ExactLabResourceInventory `
        -Resources $resources `
        -ExpectedTopLevelResources $expectedTopLevelResources `
        -AllowedResourceTypes $allowedTypes

    Write-LabStatus 'Verifying the Storage account SKU.'
    $storageSku = Invoke-AzureCli @(
        'storage', 'account', 'show', '--resource-group', $ResourceGroupName, '--name', $storageName,
        '--query', 'sku.name', '--output', 'tsv'
    )
    if ($storageSku -ne 'Standard_LRS') {
        throw "Storage '$storageName' uses '$storageSku', not Standard_LRS."
    }

    Write-LabStatus 'Verifying Cosmos DB serverless capability.'
    $cosmosCapabilities = @(Invoke-AzureCli @(
        'cosmosdb', 'show', '--resource-group', $ResourceGroupName, '--name', $cosmosName,
        '--query', 'capabilities[].name', '--output', 'tsv'
    ))
    if ('EnableServerless' -notin $cosmosCapabilities) {
        throw "Cosmos DB '$cosmosName' is not serverless."
    }

    Write-LabStatus 'Verifying the Service Bus namespace SKU.'
    $serviceBusSku = Invoke-AzureCli @(
        'servicebus', 'namespace', 'show', '--resource-group', $ResourceGroupName, '--name', $serviceBusName,
        '--query', 'sku.name', '--output', 'tsv'
    )
    if ($serviceBusSku -ne 'Standard') {
        throw "Service Bus '$serviceBusName' uses '$serviceBusSku', not Standard."
    }

    Write-LabStatus 'Verifying both Service Bus topics are partitioned.'
    foreach ($topicName in @('graceeventstream', 'grace-usage')) {
        $partitioningEnabled = Invoke-AzureCli @(
            'servicebus', 'topic', 'show',
            '--resource-group', $ResourceGroupName,
            '--namespace-name', $serviceBusName,
            '--name', $topicName,
            '--query', 'enablePartitioning',
            '--output', 'tsv'
        )
        if ($partitioningEnabled -ne 'true') {
            throw "Service Bus topic '$topicName' is not partitioned."
        }
    }

    Write-LabStatus 'Verifying Azure SQL serverless compute settings.'
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

    Write-LabStatus 'Verifying the TLS-only Azure Container Instances Redis process.'
    $redisConfiguration = Invoke-AzureCli @(
        'container', 'show', '--resource-group', $ResourceGroupName, '--name', $redisContainerGroupName,
        '--query', '{provisioningState:provisioningState,state:instanceView.state,image:containers[0].image,fqdn:ipAddress.fqdn,ports:ipAddress.ports[].port}',
        '--output', 'json'
    ) | ConvertFrom-Json
    if ($redisConfiguration.provisioningState -ne 'Succeeded' -or $redisConfiguration.state -ne 'Running' -or
        $redisConfiguration.image -ne 'redis:7.4-alpine') {
        throw "Redis container '$redisContainerGroupName' is not running the expected redis:7.4-alpine image."
    }
    if ($redisConfiguration.fqdn -ne $redisDnsName -or
        @($redisConfiguration.ports).Count -ne 1 -or
        @($redisConfiguration.ports)[0] -ne $redisTlsPort) {
        throw "Redis container '$redisContainerGroupName' does not expose only the expected TLS endpoint."
    }

    $resources | Sort-Object type | Format-Table name, type, sku -AutoSize
    Write-Host 'Live lab resource verification passed.' -ForegroundColor Green
}

Write-LabStatus "Starting Grace infrastructure lab action '$Action'."
Write-Host "Resource group:    $ResourceGroupName"
Write-Host "Deployment suffix: $DeploymentSuffix"
Write-Host "Location:          $Location"
Set-VerifiedSubscription

switch ($Action) {
    'Build' {
        Write-LabStatus 'Building both infrastructure profiles without changing Azure resources.'
        Invoke-BicepBuilds
        Write-LabStatus 'Build action completed.'
    }
    'WhatIf' {
        Write-LabStatus 'Preparing the disposable lab for ARM validation and what-if.'
        Invoke-BicepBuilds
        Register-LabResourceProviders
        New-LabResourceGroup
        $identity = Get-DeveloperIdentity
        $publicIpAddress = Get-ValidatedClientIpAddress
        $redisMaterial = New-LabRedisMaterial
        $secureParameterPath = New-SecureRedisParameterFile -Identity $identity -PublicIpAddress $publicIpAddress
        try {
            Set-LabRedisDeploymentEnvironment -Material $redisMaterial
            $arguments = Get-DeploymentArguments -SecureParameterPath $secureParameterPath
            Write-LabStatus 'Validating the lab deployment with Azure Resource Manager.'
            Invoke-AzureCli (@('deployment', 'group', 'validate') + $arguments + @('--output', 'none')) | Out-Null
            Write-LabStatus 'ARM validation passed; calculating the proposed changes.'
            Invoke-AzureCli (@('deployment', 'group', 'what-if') + $arguments + @('--no-pretty-print'))
            Write-LabStatus 'What-if action completed.'
        }
        finally {
            Clear-LabRedisSecrets
            Remove-Item -LiteralPath $secureParameterPath -Force -ErrorAction SilentlyContinue
            $redisMaterial = $null
        }
    }
    'Deploy' {
        Write-LabStatus 'Preparing to deploy the disposable infrastructure lab.'
        Invoke-BicepBuilds
        Register-LabResourceProviders
        New-LabResourceGroup
        $identity = Get-DeveloperIdentity
        $publicIpAddress = Get-ValidatedClientIpAddress
        $redisMaterial = New-LabRedisMaterial
        $secureParameterPath = New-SecureRedisParameterFile -Identity $identity -PublicIpAddress $publicIpAddress
        try {
            Set-LabRedisDeploymentEnvironment -Material $redisMaterial
            $arguments = Get-DeploymentArguments -SecureParameterPath $secureParameterPath
            Write-LabStatus 'Submitting the lab deployment. Most resources provide no intermediate ARM progress.'
            Invoke-AzureCli (@('deployment', 'group', 'create') + $arguments + @('--output', 'none')) | Out-Null
            Write-LabStatus 'Deployment completed; launching DebugAzure with the matching process-scoped Redis material.'

            $outputs = Invoke-AzureCli @(
                'deployment', 'group', 'show', '--resource-group', $ResourceGroupName, '--name', $deploymentName,
                '--query', 'properties.outputs', '--output', 'json'
            ) | ConvertFrom-Json
            $debugAzureSettings = @{
                'grace__azure_storage_account_name' = $outputs.storageAccountName.value
                'grace__azure_cosmos_db__endpoint' = $outputs.cosmosEndpoint.value
                'grace__azure_cosmos_db__database_name' = $outputs.cosmosDatabaseName.value
                'grace__azure_cosmos_db__container_name' = $outputs.cosmosContainerName.value
                'grace__azure_service_bus__namespace' = "$($outputs.serviceBusNamespace.value).servicebus.windows.net"
                'grace__azure_service_bus__topic' = $outputs.serviceBusEventTopic.value
                'grace__azure_service_bus__subscription' = $outputs.serviceBusEventSubscription.value
                'grace__azure_service_bus__operational_facts_topic' = $outputs.serviceBusGraceUsageTopic.value
                'grace__azure_service_bus__operational_facts_processor_subscription' = $outputs.serviceBusGraceUsageSubscription.value
                'grace__operations__sql__connectionstring' = $outputs.sqlConnectionString.value
                'grace__redis__host' = $redisDnsName
                'grace__redis__port' = "$redisTlsPort"
                'grace__redis__tls' = 'true'
                'grace__redis__username' = $redisMaterial.Username
                'grace__redis__password' = $redisMaterial.Password
                'grace__redis__ca_certificate' = $redisMaterial.CaBase64
            }
            foreach ($setting in $debugAzureSettings.GetEnumerator()) {
                [Environment]::SetEnvironmentVariable($setting.Key, [string] $setting.Value, 'Process')
            }

            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'start-debugazure.ps1')
            if ($LASTEXITCODE -ne 0) {
                throw 'DebugAzure failed before Redis readiness could be proven.'
            }

            Write-LabStatus 'Proving authenticated Redis PING with certificate and hostname validation.'
            Test-RedisTlsReadiness -Material $redisMaterial
            Clear-LabRedisSecrets
            $debugAzureSettings = $null
            $redisMaterial = $null
            Write-LabStatus 'DebugAzure Redis readiness passed and parent-process secrets were cleared.'
        }
        catch {
            Clear-LabRedisSecrets
            throw
        }
        finally {
            Remove-Item -LiteralPath $secureParameterPath -Force -ErrorAction SilentlyContinue
            $redisMaterial = $null
        }
    }
    'Verify' {
        Write-LabStatus 'Verifying the live lab resource inventory and SKU choices.'
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

        Write-LabStatus "Deleting disposable resource group '$resourceGroupId'."
        Invoke-AzureCli @('group', 'delete', '--name', $ResourceGroupName, '--yes', '--no-wait') | Out-Null
        Write-LabStatus 'Deletion accepted. Run VerifyRemoved until it reports success.'
    }
    'VerifyRemoved' {
        $exists = Invoke-AzureCli @('group', 'exists', '--name', $ResourceGroupName, '--output', 'tsv')
        if ($exists -ne 'false') {
            throw "Resource group '$ResourceGroupName' still exists or is deleting."
        }

        Write-LabStatus "Resource group '$ResourceGroupName' is absent."
    }
}
