#Requires -Version 7.6

function Assert-ExactLabResourceInventory {
    <#
    .SYNOPSIS
    Rejects missing, unexpected, or duplicate top-level resources in one infrastructure lab.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]] $Resources,

        [Parameter(Mandatory)]
        [object[]] $ExpectedTopLevelResources,

        [Parameter(Mandatory)]
        [string[]] $AllowedResourceTypes
    )

    $missingTopLevelResources = [System.Collections.Generic.List[string]]::new()
    foreach ($expectedResource in $ExpectedTopLevelResources) {
        $matches = @(
            $Resources | Where-Object {
                [string]::Equals($_.name, $expectedResource.name, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals($_.type, $expectedResource.type, [StringComparison]::OrdinalIgnoreCase)
            }
        )

        if ($matches.Count -ne 1) {
            $missingTopLevelResources.Add("$($expectedResource.name) [$($expectedResource.type)]")
        }
    }

    $expectedTopLevelTypes = @($ExpectedTopLevelResources.type | Sort-Object -Unique)
    $unexpectedTopLevelResources = [System.Collections.Generic.List[string]]::new()
    foreach ($resource in $Resources | Where-Object { $_.type -in $expectedTopLevelTypes }) {
        $matches = @(
            $ExpectedTopLevelResources | Where-Object {
                [string]::Equals($_.name, $resource.name, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals($_.type, $resource.type, [StringComparison]::OrdinalIgnoreCase)
            }
        )

        if ($matches.Count -ne 1) {
            $unexpectedTopLevelResources.Add("$($resource.name) [$($resource.type)]")
        }
    }

    $unexpectedResourceTypes = @(
        $Resources |
            Where-Object { $_.type -notin $AllowedResourceTypes } |
            ForEach-Object { "$($_.name) [$($_.type)]" }
    )

    if ($missingTopLevelResources.Count -gt 0 -or
        $unexpectedTopLevelResources.Count -gt 0 -or
        $unexpectedResourceTypes.Count -gt 0) {
        throw "Lab resource inventory differs. Missing top-level: $($missingTopLevelResources -join ', '); unexpected top-level: $($unexpectedTopLevelResources -join ', '); unexpected types: $($unexpectedResourceTypes -join ', ')."
    }
}
