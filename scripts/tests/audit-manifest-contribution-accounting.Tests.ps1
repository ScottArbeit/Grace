[CmdletBinding()]
param(
    [switch] $AllowUncommittedAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packetRoot = Join-Path $repositoryRoot 'artifacts/manifest-accounting-measurements'
$ledgerPath = Join-Path $repositoryRoot 'docs/Manifest contribution accounting Product V1 audit.md'
$measuredSource = '60da9740236e36bfab770a1f246f0d993740b7fc'
$artifactChild = 'dce9fe0a2677459e521b30737c296f917f7eb701'
$epicMerge = '96c3c09300d3fbe0d65fad309afe2bc3983dd280'
$epicMergeFirstParent = 'eda17e2c53995e20446808660ba278079af65397'
$expectedRunId = '0687988305d54d1eb39f7d55146a5693'
$recordByteLimit = 65536
$fileByteLimit = 2MB
$packetByteLimit = 16MB

$script:Passed = 0
$script:Failed = 0

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Equal {
    param($Actual, $Expected, [string] $Message)
    if ($Actual -cne $Expected) { throw "Assertion failed: $Message (actual '$Actual', expected '$Expected')" }
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

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $output = @(& git -C $repositoryRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)" }
    return $output
}

function Get-Sha256 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-Ndjson {
    param([string] $Path)
    $records = [Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $lineNumber++
        Assert-True (-not [string]::IsNullOrWhiteSpace($line)) "$(Split-Path -Leaf $Path):$lineNumber is blank"
        Assert-True ([Text.Encoding]::UTF8.GetByteCount($line) -le $recordByteLimit) "$(Split-Path -Leaf $Path):$lineNumber exceeds the record bound"
        $records.Add(($line | ConvertFrom-Json -Depth 40))
    }
    Assert-True ($records.Count -gt 0) "$(Split-Path -Leaf $Path) must contain records"
    return $records.ToArray()
}

function Get-DeclaredParameterNames {
    param([string] $Path)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref] $tokens, [ref] $errors)
    Assert-Equal $errors.Count 0 "$Path must parse as PowerShell"
    return @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
}

function Assert-ExactSet {
    param([object[]] $Actual, [object[]] $Expected, [string] $Message)
    $difference = @(Compare-Object @($Expected | Sort-Object) @($Actual | Sort-Object))
    Assert-Equal $difference.Count 0 $Message
}

$expectedPacketPaths = @(
    'artifact-hashes.json'
    'assertions.json'
    'logs/baseline.jsonl'
    'logs/dead-letter.jsonl'
    'logs/duplicate-backlog.jsonl'
    'logs/highly-shared.jsonl'
    'logs/hot-manifest.jsonl'
    'logs/redis-restart.jsonl'
    'logs/repair.jsonl'
    'logs/server-restart.jsonl'
    'raw/artifact-hashes.json'
    'raw/assertions.ndjson'
    'raw/run.ndjson'
    'raw/samples.ndjson'
    'raw/summaries.ndjson'
    'run.json'
    'samples.ndjson'
    'summary.json'
)

$scenarioIds = @(
    'baseline'
    'hot-manifest'
    'highly-shared'
    'duplicate-backlog'
    'redis-restart'
    'server-restart'
    'repair'
    'dead-letter'
)

$referenceTypes = @('Promotion', 'Commit', 'Checkpoint', 'Save', 'Tag', 'External', 'Rebase')
$auditOnlyPaths = @(
    'CONTEXT.md'
    'docs/Manifest contribution accounting Product V1 audit.md'
    'docs/Manifest contribution accounting runtime.md'
    'docs/Manifest contribution diagnosis.md'
    'docs/Manifest contribution repair.md'
    'docs/adr/0001-content-addressed-storage-contentblocks.md'
    'scripts/tests/audit-manifest-contribution-accounting.Tests.ps1'
    'src/docs/ASPIRE_SETUP.md'
)

Push-Location $repositoryRoot
try {
    Invoke-Case 'packet has the exact immutable 18-file tree' {
        $actual = @(Get-ChildItem -LiteralPath $packetRoot -Recurse -File | ForEach-Object {
                [IO.Path]::GetRelativePath($packetRoot, $_.FullName).Replace('\', '/')
            })
        Assert-ExactSet $actual $expectedPacketPaths 'packet tree must match the accepted 18 paths'
    }

    Invoke-Case 'root and raw hash manifests verify every retained byte' {
        $rootHashes = @(Get-Content -LiteralPath (Join-Path $packetRoot 'artifact-hashes.json') -Raw | ConvertFrom-Json)
        Assert-Equal $rootHashes.Count 17 'root hash manifest must cover every file except itself'
        Assert-ExactSet @($rootHashes.path) @($expectedPacketPaths | Where-Object { $_ -cne 'artifact-hashes.json' }) 'root hash paths must be exact'
        foreach ($entry in $rootHashes) {
            Assert-True ($entry.sha256 -cmatch '^[0-9a-f]{64}$') "root hash for $($entry.path) must be lowercase SHA-256"
            Assert-Equal (Get-Sha256 (Join-Path $packetRoot $entry.path)) $entry.sha256 "root hash for $($entry.path) must verify"
        }

        $rawRoot = Join-Path $packetRoot 'raw'
        $rawHashes = @(Get-Content -LiteralPath (Join-Path $rawRoot 'artifact-hashes.json') -Raw | ConvertFrom-Json)
        Assert-Equal $rawHashes.Count 4 'raw hash manifest must cover the four raw record files'
        Assert-ExactSet @($rawHashes.FileName) @('run.ndjson', 'samples.ndjson', 'assertions.ndjson', 'summaries.ndjson') 'raw hash paths must be exact'
        foreach ($entry in $rawHashes) {
            Assert-Equal (Get-Sha256 (Join-Path $rawRoot $entry.FileName)) $entry.Sha256 "raw hash for $($entry.FileName) must verify"
        }
        Assert-Equal (Get-Sha256 (Join-Path $packetRoot 'samples.ndjson')) (Get-Sha256 (Join-Path $rawRoot 'samples.ndjson')) 'root samples must retain raw bytes'
    }

    Invoke-Case 'packet sizes, record bounds, and redaction remain enforced' {
        $total = [long] 0
        $forbidden = '(?i)(authorization\s*:\s*bearer|accountkey|sharedaccesskey(?:name)?|client_secret|access_token|refresh_token|password)\s*[:=]'
        foreach ($file in Get-ChildItem -LiteralPath $packetRoot -Recurse -File) {
            Assert-True ($file.Length -le $fileByteLimit) "$($file.Name) exceeds the 2 MiB file bound"
            $total += $file.Length
            $text = [IO.File]::ReadAllText($file.FullName)
            Assert-True ($text -notmatch $forbidden) "$($file.Name) contains forbidden credential-shaped content"
            if ($file.Extension -in @('.ndjson', '.jsonl')) {
                $lineNumber = 0
                foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
                    $lineNumber++
                    Assert-True (-not [string]::IsNullOrWhiteSpace($line)) "$($file.Name):$lineNumber is blank"
                    Assert-True ([Text.Encoding]::UTF8.GetByteCount($line) -le $recordByteLimit) "$($file.Name):$lineNumber exceeds 65,536 bytes"
                    $line | ConvertFrom-Json -Depth 40 | Out-Null
                }
            }
        }
        Assert-True ($total -le $packetByteLimit) 'packet exceeds the 16 MiB aggregate bound'
    }

    $rawRun = @(Read-Ndjson (Join-Path $packetRoot 'raw/run.ndjson'))
    $rawSamples = @(Read-Ndjson (Join-Path $packetRoot 'raw/samples.ndjson'))
    $rawAssertions = @(Read-Ndjson (Join-Path $packetRoot 'raw/assertions.ndjson'))
    $rawSummaries = @(Read-Ndjson (Join-Path $packetRoot 'raw/summaries.ndjson'))
    $publishedRun = Get-Content -LiteralPath (Join-Path $packetRoot 'run.json') -Raw | ConvertFrom-Json
    $publishedSummary = Get-Content -LiteralPath (Join-Path $packetRoot 'summary.json') -Raw | ConvertFrom-Json
    $publishedAssertions = @(Get-Content -LiteralPath (Join-Path $packetRoot 'assertions.json') -Raw | ConvertFrom-Json)

    Invoke-Case 'source SHA, branch, dirty flag, command, and claim boundaries are truthful' {
        Assert-Equal $rawRun.Count 1 'raw run must contain exactly one record'
        Assert-Equal $rawRun[0].CommitSha $measuredSource 'raw source SHA must name the clean measured source'
        Assert-Equal $rawRun[0].Branch 'agent/752-mca-exact-sha-evidence' 'raw branch must name the measurement branch'
        Assert-True ($rawRun[0].Dirty -eq $false) 'raw source must be clean'
        Assert-Equal $publishedRun.sourceGitSha $measuredSource 'published source SHA must match raw source'
        Assert-Equal $publishedRun.sourceGitBranch $rawRun[0].Branch 'published source branch must match raw source'
        Assert-True ($publishedRun.sourceDirty -eq $false) 'published source must be clean'
        Assert-Equal $publishedRun.command "pwsh ./scripts/measure-manifest-contribution-accounting.ps1 -OutputDirectory './artifacts/manifest-accounting-measurements'" 'published command must be the actual supported invocation'
        Assert-Equal $rawRun[0].Command 'dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj --configuration Release --no-build --filter FullyQualifiedName~ManifestContributionGroupedMeasurementTests' 'raw command must name the selected grouped fixture'
        Assert-Equal $publishedRun.localClaims.Count 10 'local claim membership must remain exact'
        Assert-Equal $publishedRun.azureOnlyUnknowns.Count 6 'Azure-only unknown membership must remain exact'
        Assert-Equal (@($publishedRun.localClaims | Where-Object { $publishedRun.azureOnlyUnknowns -contains $_ }).Count) 0 'local and Azure-only claims must not overlap'
    }

    Invoke-Case '104 assertions, nine summaries, 52 samples, and one RunId remain exact' {
        Assert-Equal $rawAssertions.Count 104 'raw assertion count'
        Assert-Equal $publishedAssertions.Count 104 'published assertion count'
        Assert-Equal @($rawAssertions | Where-Object { $_.Passed -eq $true }).Count 104 'all raw assertions must pass'
        Assert-Equal @($rawAssertions.AssertionId | Sort-Object -Unique).Count 104 'assertion IDs must be unique'
        Assert-Equal $rawSummaries.Count 9 'raw summary count'
        Assert-Equal @($rawSummaries | Where-Object { $_.Outcome -ceq 'Passed' }).Count 9 'all summaries must pass'
        Assert-Equal @($rawSummaries.ScenarioId | Sort-Object -Unique).Count 9 'summary scenario IDs must be unique'
        Assert-ExactSet @($rawSummaries.ScenarioId) @($scenarioIds + 'grouped') 'summary scenario membership must be exact'
        Assert-Equal $rawSamples.Count 52 'raw sample count'
        Assert-ExactSet @($rawSamples.RunId + $rawAssertions.RunId + $rawSummaries.RunId | Sort-Object -Unique) @($expectedRunId) 'all raw records must use one RunId'
        Assert-ExactSet @($publishedAssertions.AssertionId) @($rawAssertions.AssertionId) 'published assertion membership must match raw evidence'
    }

    Invoke-Case 'summary assertion membership is exact and recomputable' {
        $summaryIds = [Collections.Generic.List[string]]::new()
        foreach ($summary in $rawSummaries) {
            Assert-Equal $summary.RequiredAssertionCount @($summary.RequiredAssertionIds).Count "$($summary.ScenarioId) required count"
            Assert-Equal @($summary.RequiredAssertionIds | Sort-Object -Unique).Count @($summary.RequiredAssertionIds).Count "$($summary.ScenarioId) required IDs must be unique"
            Assert-Equal $summary.PassedAssertionCount $summary.RequiredAssertionCount "$($summary.ScenarioId) passed count"
            Assert-Equal @($summary.FailedAssertionIds).Count 0 "$($summary.ScenarioId) failed IDs"
            Assert-Equal @($summary.RuntimeFailures).Count 0 "$($summary.ScenarioId) runtime failures"
            foreach ($id in $summary.RequiredAssertionIds) { $summaryIds.Add($id) }
        }
        Assert-ExactSet $summaryIds.ToArray() @($rawAssertions.AssertionId) 'summary-required IDs must cover every assertion exactly once'
        foreach ($assertion in $rawAssertions) {
            Assert-True ($assertion.AssertionId.StartsWith("$($assertion.ScenarioId).", [StringComparison]::Ordinal)) "$($assertion.AssertionId) must belong to its exact scenario"
        }
    }

    Invoke-Case 'published totals and 30 numeric aggregates recompute from raw evidence' {
        Assert-Equal $publishedSummary.assertionTotals.total 104 'published total assertions'
        Assert-Equal $publishedSummary.assertionTotals.passed 104 'published passed assertions'
        Assert-Equal $publishedSummary.assertionTotals.failed 0 'published failed assertions'
        Assert-Equal $publishedSummary.sampleTotals.total 52 'published total samples'
        $phaseSnapshots = @($rawSamples | Where-Object {
                $phase = $_.Labels.PSObject.Properties['phase']
                $null -ne $phase -and $phase.Value -ceq 'stimulus'
            })
        Assert-Equal $publishedSummary.sampleTotals.phaseSnapshots $phaseSnapshots.Count 'phase snapshots must recompute'
        Assert-Equal $publishedSummary.numericAggregates.Count 30 'numeric aggregate count'
        foreach ($aggregate in $publishedSummary.numericAggregates) {
            $values = @($rawSamples | Where-Object { $_.ScenarioId -ceq $aggregate.scenarioId -and $_.Name -ceq $aggregate.name } | ForEach-Object { [double] $_.Value })
            Assert-Equal $aggregate.count $values.Count "$($aggregate.scenarioId)/$($aggregate.name) aggregate count"
            Assert-Equal ([double] $aggregate.minimum) ([double] ($values | Measure-Object -Minimum).Minimum) "$($aggregate.scenarioId)/$($aggregate.name) minimum"
            Assert-Equal ([double] $aggregate.maximum) ([double] ($values | Measure-Object -Maximum).Maximum) "$($aggregate.scenarioId)/$($aggregate.name) maximum"
            Assert-Equal ([double] $aggregate.sum) ([double] ($values | Measure-Object -Sum).Sum) "$($aggregate.scenarioId)/$($aggregate.name) sum"
        }
    }

    Invoke-Case 'Git provenance preserves distinct source, artifact, merge, and audit identities' {
        $artifactParent = [string] (@(Invoke-Git rev-parse "$artifactChild^")[-1])
        Assert-Equal $artifactParent $measuredSource 'artifact child must immediately follow measured source'
        $mergeLine = [string] (@(Invoke-Git rev-list --parents -n 1 $epicMerge)[-1])
        $mergeParents = @($mergeLine -split ' ')
        Assert-ExactSet @($mergeParents[1], $mergeParents[2]) @($epicMergeFirstParent, $artifactChild) 'Epic merge parents must be exact'
        Invoke-Git merge-base --is-ancestor $measuredSource HEAD | Out-Null
        Invoke-Git merge-base --is-ancestor $artifactChild HEAD | Out-Null
        Invoke-Git merge-base --is-ancestor $epicMerge HEAD | Out-Null
        $auditHead = (Invoke-Git rev-parse HEAD)[-1]
        if (-not $AllowUncommittedAudit) {
            Assert-True ($auditHead -notin @($measuredSource, $artifactChild, $epicMerge)) 'final audit head must be a fourth Git identity'
        }

        $artifactCommitPaths = @(Invoke-Git diff-tree --no-commit-id --name-only -r $artifactChild)
        Assert-ExactSet $artifactCommitPaths @($expectedPacketPaths | ForEach-Object { "artifacts/manifest-accounting-measurements/$_" }) 'artifact child must contain only the exact packet'
        Assert-Equal @(Invoke-Git diff --name-only $artifactChild HEAD -- artifacts/manifest-accounting-measurements).Count 0 'packet bytes must remain identical to the artifact child'
    }

    Invoke-Case 'measured publisher, runtime, fixture, product, public, durable, and generated surfaces are unchanged' {
        $changed = @(Invoke-Git diff --name-only "$measuredSource..HEAD")
        if ($AllowUncommittedAudit) {
            $changed += @(Invoke-Git diff --name-only)
            $changed += @(Invoke-Git diff --cached --name-only)
            $changed += @(Invoke-Git ls-files --others --exclude-standard)
        }
        foreach ($path in @($changed | Sort-Object -Unique)) {
            $allowed = $path.StartsWith('artifacts/manifest-accounting-measurements/', [StringComparison]::Ordinal) -or $auditOnlyPaths -contains $path
            Assert-True $allowed "measured surface changed outside the audit allowlist: $path"
        }
    }

    Invoke-Case 'operator scripts parse and retain exact maintained parameter contracts' {
        Assert-ExactSet (Get-DeclaredParameterNames (Join-Path $repositoryRoot 'scripts/measure-manifest-contribution-accounting.ps1')) @('OutputDirectory') 'measurement parameters'
        Assert-ExactSet (Get-DeclaredParameterNames (Join-Path $repositoryRoot 'scripts/diagnose-manifest-contribution.ps1')) @('ReferenceId', 'DirectoryVersionId', 'RepositoryId', 'StoragePoolId', 'ManifestAddress', 'RepositoryContentCounterOperationId', 'MaxRelationships', 'OutputPath') 'diagnosis parameters'
        Assert-ExactSet (Get-DeclaredParameterNames (Join-Path $repositoryRoot 'scripts/repair-manifest-contribution.ps1')) @('ReportPath', 'ExpectedReportSha256', 'Execute', 'OutputPath') 'repair parameters'
    }

    Invoke-Case 'runbooks match commands, routes, outcomes, exit codes, and dry-run execution contracts' {
        $runtime = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs/Manifest contribution accounting runtime.md') -Raw
        $diagnosis = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs/Manifest contribution diagnosis.md') -Raw
        $repair = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs/Manifest contribution repair.md') -Raw
        $startup = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Grace.Server/Startup.Server.fs') -Raw
        $authorization = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Grace.Server/Security/EndpointAuthorizationManifest.Server.fs') -Raw
        foreach ($token in @($measuredSource, $artifactChild, $epicMerge, '104 unique passed assertions', '52 samples', 'localClaims', 'azureOnlyUnknowns')) {
            Assert-True $runtime.Contains($token, [StringComparison]::Ordinal) "runtime runbook must name $token"
        }
        foreach ($token in @('diagnose-manifest-contribution.ps1', '-MaxRelationships 5000', 'VerifiedComplete', 'IncompleteRetain', 'FailedRetain', '| 0 |', '| 2 |', '| 3 |', '| 4 |')) {
            Assert-True $diagnosis.Contains($token, [StringComparison]::Ordinal) "diagnosis runbook must name $token"
        }
        foreach ($token in @('repair-manifest-contribution.ps1', '-ExpectedReportSha256', '-Execute', 'Dry run is the default', 'VerifiedComplete', 'IncompleteRetain', 'FailedRetain', '| 0 |', '| 2 |', '| 3 |', '| 4 |')) {
            Assert-True $repair.Contains($token, [StringComparison]::Ordinal) "repair runbook must name $token"
        }
        foreach ($route in @('diagnose', 'repair')) {
            Assert-True $startup.Contains("/manifest-contribution/$route", [StringComparison]::Ordinal) "Startup must retain the $route route under /admin"
            Assert-True $authorization.Contains("/admin/manifest-contribution/$route", [StringComparison]::Ordinal) "authorization manifest must retain the full $route route"
        }
    }

    Invoke-Case 'all seven ReferenceType values remain uniform without accounting Save specialization' {
        $common = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Grace.Types/Common.Types.fs') -Raw
        $typeMatch = [regex]::Match($common, 'type ReferenceType\s*=\s*(?<body>.*?)\s+static member', 'Singleline')
        Assert-True $typeMatch.Success 'ReferenceType declaration must remain discoverable'
        $declared = @([regex]::Matches($typeMatch.Groups['body'].Value, '\|\s+(?<name>[A-Za-z]+)') | ForEach-Object { $_.Groups['name'].Value })
        Assert-ExactSet $declared $referenceTypes 'ReferenceType cases must remain exact'

        $accountingSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Grace.Server/ManifestContributionAccounting.Server.fs') -Raw
        Assert-True (-not $accountingSource.Contains('ReferenceType.Save', [StringComparison]::Ordinal)) 'accounting production path must not specialize Save'
        foreach ($testPath in @(
                'src/Grace.Server.Unit.Tests/ManifestContributionAccounting.Server.Tests.fs'
                'src/Grace.Server.Unit.Tests/Reference.Actor.Tests.fs'
                'src/Grace.Server.Unit.Tests/ManifestContributionRepair.Server.Tests.fs'
            )) {
            $testText = Get-Content -LiteralPath (Join-Path $repositoryRoot $testPath) -Raw
            foreach ($referenceType in $referenceTypes) {
                Assert-True $testText.Contains("ReferenceType.$referenceType", [StringComparison]::Ordinal) "$testPath must represent $referenceType"
            }
        }
    }

    Invoke-Case 'coverage ledger is complete and every non-required capability has an explicit disposition' {
        $ledger = Get-Content -LiteralPath $ledgerPath -Raw
        foreach ($id in @('MCA-00', 'MCA-01', 'MCA-02', 'MCA-03', 'MCA-04', 'MCA-05', 'MCA-06', 'MCA-07', 'MCA-08', 'MCA-08A', 'MCA-08B', 'MCA-08B-R0', 'MCA-08B-R1', 'MCA-08B-R2', 'MCA-08B-R3', 'MCA-08B-R4', 'MCA-08B-R5', 'MCA-08B-R6', 'MCA-08B-R7', 'MCA-08B-R8', 'MCA-08C', 'MCA-09', 'MCA-08B-R8A')) {
            Assert-True $ledger.Contains("| $id |", [StringComparison]::Ordinal) "coverage ledger must contain $id"
        }
        foreach ($token in @('Accepted risk', 'Deferred', 'Rejected', 'Not applicable', 'superseded', 'Redis compound-interruption')) {
            Assert-True $ledger.Contains($token, [StringComparison]::OrdinalIgnoreCase) "coverage ledger must name disposition $token"
        }
        foreach ($path in @('src/Grace.Types/ManifestContributionAccounting.Types.fs', 'src/Grace.Server/ManifestContributionAccounting.Server.fs', 'scripts/diagnose-manifest-contribution.ps1', 'scripts/repair-manifest-contribution.ps1', 'scripts/measure-manifest-contribution-accounting.ps1', 'artifacts/manifest-accounting-measurements/run.json')) {
            Assert-True $ledger.Contains($path, [StringComparison]::Ordinal) "coverage ledger must link current evidence $path"
            Assert-True (Test-Path -LiteralPath (Join-Path $repositoryRoot $path)) "ledger evidence path must exist: $path"
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Manifest contribution Product V1 audit: $($script:Passed) passed, $($script:Failed) failed."
if ($script:Failed -ne 0) { exit 1 }
