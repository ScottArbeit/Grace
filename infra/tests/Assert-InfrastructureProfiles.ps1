#Requires -Version 7.6

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$infraRoot = Split-Path -Parent $PSScriptRoot
$labTemplatePath = Join-Path $infraRoot 'main.lab.bicep'
$productionTemplatePath = Join-Path $infraRoot 'main.production.bicep'

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

Assert-Pattern $labTemplate 'serverless:\s*true' 'The lab profile must use serverless Cosmos and SQL modules.'
Assert-Pattern $labTemplate "skuName:\s*'GP_S_Gen5_1'" 'The lab profile must use the agreed SQL serverless SKU.'
Assert-Pattern $labTemplate "skuName:\s*'Balanced_B0'" 'The lab profile must use the low-cost Redis B0 SKU.'
Assert-Pattern $labTemplate 'highAvailability:\s*false' 'The lab profile must disable Redis high availability.'

Assert-Pattern $productionTemplate 'serverless:\s*false' 'The production-shaped profile must use provisioned Cosmos and SQL modules.'
Assert-Pattern $productionTemplate 'param\s+cosmosProvisionedThroughput\s+int(\s|$)' 'The production-shaped profile must require Cosmos throughput.'
Assert-Pattern $productionTemplate 'param\s+sqlSkuName\s+string(\s|$)' 'The production-shaped profile must require a SQL SKU.'
Assert-Pattern $productionTemplate 'validatedSqlSkuName' 'The production-shaped profile must reject SQL serverless SKU names.'
Assert-Pattern $productionTemplate 'param\s+redisSkuName\s+string(\s|$)' 'The production-shaped profile must require a Redis SKU.'
Assert-Pattern $productionTemplate 'highAvailability:\s*true' 'The production-shaped profile must enable Redis high availability.'
Assert-PatternAbsent $productionTemplate "skuName:\s*'GP_S_" 'The production-shaped profile must not embed a SQL serverless SKU.'

Write-Host 'Infrastructure profile assertions passed.' -ForegroundColor Green
