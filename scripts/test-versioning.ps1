#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'src'
$violations = [System.Collections.Generic.List[string]]::new()

$projectFiles = Get-ChildItem -LiteralPath $src -Recurse -File -Include '*.fsproj', '*.csproj'

foreach ($file in $projectFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName)

    if ($text -match '<Version>\s*0\.1\s*</Version>') {
        $violations.Add("${relativePath}: contains a project-level <Version>0.1</Version> declaration.")
    }

    if ($text -match '<ContainerImageTags>\s*0\.1;latest\s*</ContainerImageTags>') {
        $violations.Add("${relativePath}: contains hard-coded 0.1;latest container tags.")
    }
}

$configSchemaFile = Join-Path $src 'Grace.Shared\Constants.Shared.fs'
$configSchemaText = Get-Content -LiteralPath $configSchemaFile -Raw

if ($configSchemaText -notmatch 'CurrentConfigurationVersion\s*=\s*"0\.1"') {
    $violations.Add('src/Grace.Shared/Constants.Shared.fs: CurrentConfigurationVersion changed; confirm this is an intentional configuration schema migration.')
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Grace versioning audit passed.'
