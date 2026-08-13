[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$canonicalPath = Join-Path $repositoryRoot 'docs/Working Directory Update.md'
$modulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleProjection.psm1'
$contractModulePath = Join-Path $repositoryRoot 'scripts/modules/WduLifecycleContract.psm1'
$packetCommand = Join-Path $repositoryRoot 'scripts/test-wdu-lifecycle-packet.ps1'

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Fails {
    param([scriptblock] $Body, [string] $Contains)
    try { & $Body | Out-Null }
    catch {
        Assert-True $_.Exception.Message.Contains($Contains, [StringComparison]::Ordinal) "failure should contain '$Contains': $($_.Exception.Message)"
        return
    }
    throw "Expected failure containing '$Contains'"
}

function Invoke-Case {
    param([string] $Name, [scriptblock] $Body)
    try { & $Body; $script:Passed++; Write-Host "PASS $Name" }
    catch { $script:Failed++; Write-Host "FAIL $Name`: $($_.Exception.Message)" -ForegroundColor Red }
}

function Write-TestText {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-Hashes {
    param([string[]] $Path)
    return @($Path | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
}

function New-Packet {
    param([string] $Name)
    $directory = Join-Path $script:TestRoot $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    foreach ($artifact in $script:Compiled.Artifacts) {
        Write-TestText (Join-Path $directory ($artifact.Id + '.md')) ($script:Projections[$artifact.Id] + "`n")
    }
    return $directory
}

Import-Module $modulePath -Force
Import-Module $contractModulePath -Force
$script:Compiled = Read-WduLifecycleContract -Path $canonicalPath
$script:Projections = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($artifact in $script:Compiled.Artifacts) {
    $script:Projections.Add($artifact.Id, (New-WduLifecycleProjection -CanonicalPath $canonicalPath -Artifact $artifact.Id))
}
$script:Passed = 0
$script:Failed = 0
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('wdu-lifecycle-packet-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null

try {
    Invoke-Case 'checks only a complete compiler-declared packet and stays read-only' {
        $packet = New-Packet 'check'
        $paths = @(Get-ChildItem -LiteralPath $packet -File | Select-Object -ExpandProperty FullName)
        $before = Get-Hashes $paths
        $result = @(& $packetCommand -CanonicalPath $canonicalPath -PacketDirectory $packet)
        Assert-True ($result.Count -eq $script:Compiled.Artifacts.Count) 'check returns every compiled artifact'
        Assert-True ((Get-Hashes $paths) -join '|' -ceq ($before -join '|')) 'check performs no writes'
    }

    Invoke-Case 'rejects renamed, extra, and case-changed packet artifacts' {
        foreach ($name in @('issue-928-renamed.md', 'ISSUE-928.md', 'unknown.md')) {
            $packet = New-Packet ([guid]::NewGuid().ToString('N'))
            $source = Join-Path $packet 'issue-928.md'
            Move-Item -LiteralPath $source -Destination (Join-Path $packet $name)
            Assert-Fails { & $packetCommand -CanonicalPath $canonicalPath -PacketDirectory $packet } 'artifact path name must equal'
        }
    }

    Invoke-Case 'renders only after complete preflight and validates the rendered packet' {
        $packet = New-Packet 'render'
        $output = Join-Path $script:TestRoot 'rendered'
        $rendered = @(& $packetCommand -CanonicalPath $canonicalPath -PacketDirectory $packet -RenderDirectory $output)
        Assert-True ($rendered.Count -eq $script:Compiled.Artifacts.Count) 'render writes every declared artifact'
        @(& $packetCommand -CanonicalPath $canonicalPath -PacketDirectory $output) | Out-Null
        Assert-Fails { & $packetCommand -CanonicalPath $canonicalPath -PacketDirectory $packet -RenderDirectory $output } 'render destination already exists'
    }
}
finally {
    if (Test-Path -LiteralPath $script:TestRoot) { Remove-Item -LiteralPath $script:TestRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
