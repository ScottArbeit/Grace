#Requires -Version 7.6
<#
.SYNOPSIS
Applies the two catalog-history guards only to an archived disposable source tree.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$adoptionSource = [IO.Path]::GetFullPath($SourceRoot)
if (Test-Path -LiteralPath (Join-Path $adoptionSource '.git')) { throw 'A Git checkout is not an admissible experiment target.' }
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $adoptionSource -Parent) 'source.zip'))) { throw 'Expected an archived experiment source.' }

function Set-AdoptionFragment {
    <# .SYNOPSIS Replaces one exact source anchor and refuses drift or a repeated application. #>
    param([string]$RelativePath, [string]$Before, [string]$After)
    $adoptionPath = Join-Path $adoptionSource $RelativePath
    $adoptionText = [IO.File]::ReadAllText($adoptionPath).Replace("`r`n", "`n")
    $adoptionBefore = $Before.Replace("`r`n", "`n")
    $adoptionAfter = $After.Replace("`r`n", "`n")
    if ([regex]::Matches($adoptionText, [regex]::Escape($adoptionBefore)).Count -ne 1) { throw "Expected one anchor in $RelativePath." }
    [IO.File]::WriteAllText($adoptionPath, $adoptionText.Replace($adoptionBefore, $adoptionAfter), [Text.UTF8Encoding]::new($false))
}

$adoptionBefore = @'
           || change.LibraryCatalogVersion
              <> expected.Catalog.Version then
'@
$adoptionAfter = @'
           || (change.LibraryCatalogVersion <> expected.Catalog.Version
               && not (operation.Direction = "remote"
                       && expected.Catalog.Libraries.Length = 2
                       && expected.Catalog.PreviousVersion = Some change.LibraryCatalogVersion)) then
'@
Set-AdoptionFragment 'src/Grace.CLI/Library/LibraryLocalState.CLI.fs' $adoptionBefore $adoptionAfter
$adoptionBefore = @'
            do! checkCatalog configuration correlationId expected
            captureSaved configuration |> ignore
'@
$adoptionAfter = @'
            do! checkCatalog configuration correlationId expected
            // Disposable adoption experiment: keep original acceptance metadata and reject unsupported catalog history before effects.
            if change.LibraryCatalogVersion <> expected.Catalog.Version
               && not (expected.Catalog.Libraries.Length = 2
                       && expected.Catalog.PreviousVersion = Some change.LibraryCatalogVersion) then
                invalidOp "Prototype adoption rejects unsupported accepted catalog history."
            captureSaved configuration |> ignore
'@
Set-AdoptionFragment 'src/Grace.CLI/Library/LibrarySynchronization.CLI.fs' $adoptionBefore $adoptionAfter
