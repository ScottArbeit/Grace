[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$getCommand = Join-Path $repositoryRoot 'scripts/get-wdu-lifecycle-projection.ps1'
$rawProjectionModule = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleRawProjection.psm1'
$contractModule = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleContract.psm1'
$canonicalPath = Join-Path $repositoryRoot 'docs/Working Directory Update.md'

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-ExactBytes {
    param([byte[]] $Actual, [byte[]] $Expected, [string] $Message)
    Assert-True ($Actual.Length -eq $Expected.Length) "$Message length"
    Assert-True ([Convert]::ToHexString($Actual) -ceq [Convert]::ToHexString($Expected)) "$Message bytes"
}

function Invoke-Case {
    param([string] $Name, [scriptblock] $Body)
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Invoke-WduExportProcess {
    param(
        [string[]] $CommandArguments = @(),
        [string] $ScriptText
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void] $startInfo.ArgumentList.Add('-NoLogo')
    [void] $startInfo.ArgumentList.Add('-NoProfile')
    if (-not [string]::IsNullOrEmpty($ScriptText)) {
        [void] $startInfo.ArgumentList.Add('-Command')
        [void] $startInfo.ArgumentList.Add($ScriptText)
    }
    else {
        [void] $startInfo.ArgumentList.Add('-File')
        [void] $startInfo.ArgumentList.Add($getCommand)
        foreach ($argument in $CommandArguments) { [void] $startInfo.ArgumentList.Add($argument) }
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void] $process.Start()
        $stdout = [IO.MemoryStream]::new()
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $null = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.ToArray()
            Stderr = $stderr
        }
    }
    finally {
        if ($null -ne $stdout) { $stdout.Dispose() }
        $process.Dispose()
    }
}

function ConvertTo-SingleQuotedPowerShellLiteral {
    param([string] $Value)
    return "'$($Value.Replace("'", "''"))'"
}

Import-Module $rawProjectionModule -Force
Import-Module $contractModule -Force
$compiled = Read-WduLifecycleContract -Path $canonicalPath
$artifactIds = @($compiled.Artifacts | ForEach-Object { $_.Id })
$script:Passed = 0
$script:Failed = 0

Invoke-Case 'reproduces the superseded contract-then-raw import failure' {
    $contractLiteral = ConvertTo-SingleQuotedPowerShellLiteral $contractModule
    $rawLiteral = ConvertTo-SingleQuotedPowerShellLiteral $rawProjectionModule
    $oldComposition = "Import-Module $contractLiteral -Force; Import-Module $rawLiteral -Force; if (Get-Command Read-WduLifecycleContract -ErrorAction SilentlyContinue) { exit 0 }; [Console]::Error.Write('Read-WduLifecycleContract is unavailable after contract-then-raw import.'); exit 1"
    $result = Invoke-WduExportProcess -ScriptText $oldComposition
    Assert-True ($result.ExitCode -ne 0) 'old composition fails'
    Assert-True ($result.Stdout.Length -eq 0) 'old composition writes no stdout'
    Assert-True ($result.Stderr -ceq 'Read-WduLifecycleContract is unavailable after contract-then-raw import.') 'old composition identifies the missing compiler command'
}

Invoke-Case 'keeps compiler and raw projector commands available in corrected order' {
    $contractLiteral = ConvertTo-SingleQuotedPowerShellLiteral $contractModule
    $rawLiteral = ConvertTo-SingleQuotedPowerShellLiteral $rawProjectionModule
    $correctedComposition = "Import-Module $rawLiteral -Force; Import-Module $contractLiteral -Force; if (-not (Get-Command Read-WduLifecycleContract -ErrorAction SilentlyContinue)) { [Console]::Error.Write('Read-WduLifecycleContract is unavailable.'); exit 1 }; if (-not (Get-Command New-WduLifecycleRawProjection -ErrorAction SilentlyContinue)) { [Console]::Error.Write('New-WduLifecycleRawProjection is unavailable.'); exit 1 }; exit 0"
    $result = Invoke-WduExportProcess -ScriptText $correctedComposition
    Assert-True ($result.ExitCode -eq 0) 'corrected composition succeeds'
    Assert-True ($result.Stdout.Length -eq 0) 'corrected composition writes no stdout'
    Assert-True ([string]::IsNullOrEmpty($result.Stderr)) 'corrected composition writes no stderr'
}

Invoke-Case 'exports every compiler artifact as deterministic exact raw bytes from fresh processes' {
    Assert-True ($artifactIds.Count -eq 15) 'compiler declares exactly fifteen artifacts'
    foreach ($artifactId in $artifactIds) {
        $expected = (New-WduLifecycleRawProjection -Compiled $compiled -Artifact $artifactId).Utf8Json.ToArray()
        $arguments = @('-CanonicalPath', $canonicalPath, '-Artifact', $artifactId)
        $first = Invoke-WduExportProcess -CommandArguments $arguments
        $second = Invoke-WduExportProcess -CommandArguments $arguments
        Assert-True ($first.ExitCode -eq 0) "$artifactId first process exits zero"
        Assert-True ($second.ExitCode -eq 0) "$artifactId second process exits zero"
        Assert-True ([string]::IsNullOrEmpty($first.Stderr)) "$artifactId first process writes no stderr"
        Assert-True ([string]::IsNullOrEmpty($second.Stderr)) "$artifactId second process writes no stderr"
        Assert-ExactBytes $first.Stdout $expected "$artifactId first process equals #935 raw payload"
        Assert-ExactBytes $second.Stdout $expected "$artifactId second process equals #935 raw payload"
        Assert-ExactBytes $second.Stdout $first.Stdout "$artifactId fresh processes are byte-identical"
        Assert-True ($first.Stdout.Length -gt 0) "$artifactId payload is not empty"
        Assert-True ($first.Stdout[0] -eq $expected[0]) "$artifactId first byte is exact"
        Assert-True ($first.Stdout[$first.Stdout.Length - 1] -eq $expected[$expected.Length - 1]) "$artifactId last byte is exact"
        Assert-True ($first.Stdout[0] -ne 0xEF) "$artifactId payload has no UTF-8 BOM"
        Assert-True ($first.Stdout[$first.Stdout.Length - 1] -ne 0x0A -and $first.Stdout[$first.Stdout.Length - 1] -ne 0x0D) "$artifactId payload has no terminator"
    }
}

foreach ($failure in @(
        [pscustomobject]@{ Name = 'missing artifact'; Arguments = @(); Diagnostic = 'WDU raw lifecycle export: artifact is required.' + [Environment]::NewLine },
        [pscustomobject]@{ Name = 'unknown artifact'; Arguments = @('-Artifact', 'not-a-declared-artifact'); Diagnostic = "WDU raw lifecycle projection '$.artifact': artifact 'not-a-declared-artifact' is not declared by the lifecycle compiler" + [Environment]::NewLine },
        [pscustomobject]@{ Name = 'case-changed artifact'; Arguments = @('-Artifact', $artifactIds[0].ToUpperInvariant()); Diagnostic = "WDU raw lifecycle projection '$.artifact': artifact '$($artifactIds[0].ToUpperInvariant())' is not declared by the lifecycle compiler" + [Environment]::NewLine }
    )) {
    Invoke-Case "rejects $($failure.Name) without stdout" {
        $result = Invoke-WduExportProcess -CommandArguments $failure.Arguments
        Assert-True ($result.ExitCode -ne 0) "$($failure.Name) exits nonzero"
        Assert-True ($result.Stdout.Length -eq 0) "$($failure.Name) writes no stdout"
        Assert-True ($result.Stderr -ceq $failure.Diagnostic) "$($failure.Name) writes its exact scoped diagnostic"
    }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
