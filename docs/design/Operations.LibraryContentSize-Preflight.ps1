#Requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output)) { throw 'Use a new disposable output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null

# Build current production records and readers with their runtime dependencies outside the checkout.
dotnet build "$repository/src/Grace.Actors/Grace.Actors.fsproj" -c Release -p:CopyLocalLockFileAssemblies=true --artifacts-path "$output/build" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The production reader build failed.' }
$binary = "$output/build/bin/Grace.Actors/release"
$references = @('#I "' + $binary.Replace('\','/') + '"')
$references += Get-ChildItem -LiteralPath $binary -Filter '*.dll' | Where-Object Name -ne 'FSharp.Core.dll' | ForEach-Object {
    try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; '#r "' + $_.Name + '"' } catch {}
}
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$provider = Join-Path $packages 'microsoft.orleans.persistence.cosmos/10.2.2-hpk.1/lib/net10.0/Orleans.Persistence.Cosmos.dll'
$references += '#r "' + $provider.Replace('\','/') + '"'
$references += '#load "' + "$repository/src/Grace.Server/Library.Persistence.Server.fs".Replace('\','/') + '"'
Copy-Item -LiteralPath "$PSScriptRoot/Operations.LibraryContentSize-Preflight.fsx" -Destination "$output/Experiment.fsx"
$references += '#load "Experiment.fsx"'
$references | Set-Content -LiteralPath "$output/Load.fsx"

# The separately started disposable emulator must listen at https://127.0.0.1:18090.
dotnet fsi "$output/Load.fsx" *> "$output/result.log"
$result = $LASTEXITCODE
Get-Content -LiteralPath "$output/result.log" -Tail 24
if ($result -ne 0) { throw 'The bounded Library provider experiment failed.' }
Get-FileHash -Algorithm SHA256 -LiteralPath $provider, "$binary/Grace.Actors.dll", "$output/Experiment.fsx" |
    Select-Object Path, Hash | ConvertTo-Json | Set-Content -LiteralPath "$output/input-hashes.json"
