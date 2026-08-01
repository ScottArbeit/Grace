[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:McaScenarioIds = @(
    'baseline', 'hot-manifest', 'highly-shared', 'duplicate-backlog',
    'redis-restart', 'server-restart', 'repair', 'dead-letter'
)
$script:McaGroupedScenarioIds = @($script:McaScenarioIds) + 'grouped'
$script:McaRequiredAssertionIds = @(
    'baseline.duration-delta', 'baseline.evidence-integrity', 'baseline.identity-isolation',
    'baseline.logical-counts', 'baseline.manifest-relationship-set', 'baseline.message-delta',
    'baseline.physical-active-counts', 'baseline.reference-root-set', 'baseline.setup-deliveries-completed',
    'baseline.stimulus-deliveries-completed', 'baseline.workflow-counts',
    'hot-manifest.duration-delta', 'hot-manifest.evidence-integrity', 'hot-manifest.identity-isolation',
    'hot-manifest.logical-count', 'hot-manifest.manifest-relationship-cardinality', 'hot-manifest.message-delta',
    'hot-manifest.physical-active-count', 'hot-manifest.reference-root-cardinality',
    'hot-manifest.setup-deliveries-completed', 'hot-manifest.stimulus-deliveries-completed',
    'hot-manifest.workflow-count',
    'highly-shared.duration-delta', 'highly-shared.evidence-integrity', 'highly-shared.identity-isolation',
    'highly-shared.logical-count', 'highly-shared.manifest-relationship-cardinality', 'highly-shared.message-delta',
    'highly-shared.physical-active-count', 'highly-shared.reference-root-cardinality',
    'highly-shared.setup-deliveries-completed', 'highly-shared.stimulus-deliveries-completed',
    'highly-shared.workflow-count',
    'duplicate-backlog.evidence-integrity', 'duplicate-backlog.fresh-server-readiness',
    'duplicate-backlog.identity-isolation', 'duplicate-backlog.logical-state-unchanged',
    'duplicate-backlog.manifest-state-unchanged', 'duplicate-backlog.physical-state-unchanged',
    'duplicate-backlog.pre-stop-terminal-barrier', 'duplicate-backlog.reference-root-state-unchanged',
    'duplicate-backlog.replay-duration-delta', 'duplicate-backlog.replay-message-delta',
    'duplicate-backlog.seed-deliveries-completed', 'duplicate-backlog.unrelated-event-excluded',
    'duplicate-backlog.visible-while-stopped', 'duplicate-backlog.workflow-state-unchanged',
    'redis-restart.branch-setup-delivery-completed', 'redis-restart.command-completed',
    'redis-restart.evidence-integrity', 'redis-restart.fresh-health', 'redis-restart.logical-count-plus-one',
    'redis-restart.manifest-relationship-present', 'redis-restart.physical-active-count-one',
    'redis-restart.protocol-ready', 'redis-restart.reference-root-present',
    'redis-restart.seed-deliveries-completed', 'redis-restart.stimulus-duration-delta',
    'redis-restart.stimulus-message-delta', 'redis-restart.workflow-unchanged',
    'server-restart.command-completed', 'server-restart.evidence-integrity', 'server-restart.fresh-health',
    'server-restart.http-ready', 'server-restart.logical-state-unchanged',
    'server-restart.manifest-state-unchanged', 'server-restart.physical-state-unchanged',
    'server-restart.reference-root-state-unchanged', 'server-restart.replay-duration-delta',
    'server-restart.replay-message-delta', 'server-restart.seed-deliveries-completed',
    'server-restart.workflow-state-unchanged',
    'repair.corruption-applied', 'repair.diagnosis-one-supported-action', 'repair.dry-run-no-mutation',
    'repair.evidence-integrity', 'repair.execute-one-action', 'repair.logical-state-unchanged',
    'repair.physical-state-unchanged', 'repair.reference-root-restored',
    'repair.republication-duration-delta', 'repair.republication-message-delta',
    'repair.seed-deliveries-completed', 'repair.workflow-state-unchanged',
    'dead-letter.below-maximum-remains-active', 'dead-letter.cleanup-complete',
    'dead-letter.delivery-count-eleven', 'dead-letter.dlq-message-observed', 'dead-letter.evidence-integrity',
    'dead-letter.message-identity-exact', 'dead-letter.production-manifest-telemetry-unchanged',
    'dead-letter.reason-bounded-nonempty', 'dead-letter.test-subscription-isolated',
    'grouped.artifact-hashes', 'grouped.canonical-plan-order', 'grouped.cross-scenario-identity-isolation',
    'grouped.exact-epic-head-sha', 'grouped.lifecycle-dependency-propagation',
    'grouped.local-vs-azure-claim-boundary', 'grouped.no-unknown-assertion-ids',
    'grouped.records-bounded', 'grouped.records-parseable',
    'grouped.required-assertion-id-coverage', 'grouped.required-scenario-outcomes'
)
$script:McaRawFileNames = @('run.ndjson', 'samples.ndjson', 'assertions.ndjson', 'summaries.ndjson', 'artifact-hashes.json')
$script:McaRecordLimit = 65536
$script:McaFileLimit = 2MB
$script:McaPacketLimit = 16MB
$script:McaSchemaVersion = '1.0'

function Throw-McaFailure {
    param([string] $Rule, [string] $Location)
    $safeRule = if ($Rule.Length -gt 160) { $Rule.Substring(0, 160) } else { $Rule }
    $safeLocation = if ($Location.Length -gt 240) { $Location.Substring(0, 240) } else { $Location }
    throw [System.IO.InvalidDataException]::new("MCA publication failed: rule=$safeRule; location=$safeLocation")
}

function Get-McaSha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-McaFileSize {
    param([string] $Path)
    if ((Get-Item -LiteralPath $Path).Length -gt $script:McaFileLimit) {
        Throw-McaFailure 'file-size-limit' (Split-Path -Leaf $Path)
    }
}

function Assert-McaPacketSize {
    param([string] $Root)
    $packetBytes = (Get-ChildItem -LiteralPath $Root -Recurse -File | Measure-Object Length -Sum).Sum
    if ($packetBytes -gt $script:McaPacketLimit) { Throw-McaFailure 'packet-size-limit' (Split-Path -Leaf $Root) }
}

function Assert-McaPublishedHashes {
    param([string] $Root)
    $manifestPath = Join-Path $Root 'artifact-hashes.json'
    try { $entries = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 10) }
    catch { Throw-McaFailure 'published-hash-schema' 'artifact-hashes.json' }
    $otherFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object FullName -CNE $manifestPath)
    if ($entries.Count -ne $otherFiles.Count) { Throw-McaFailure 'published-hash-file-set' 'artifact-hashes.json' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        Assert-McaExactKeys $entry @('path', 'sha256') 'artifact-hashes.json'
        if (-not $seen.Add($entry.path) -or $entry.path -cnotmatch '^[^\\]+(?:/[^\\]+)*$' -or
            $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') { Throw-McaFailure 'published-hash-schema' 'artifact-hashes.json' }
        $path = Join-Path $Root $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or $entry.sha256 -cne (Get-McaSha256 $path)) {
            Throw-McaFailure 'published-hash-mismatch' $entry.path
        }
    }
}

function Assert-McaTools {
    param([string[]] $Names)
    foreach ($tool in $Names) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Throw-McaFailure 'missing-tool' $tool }
    }
}

function Get-McaCleanSource {
    param([string] $RepositoryRoot)
    $status = & git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $status) { Throw-McaFailure 'dirty-source-worktree' 'repository-root' }
    return @{
        Sha = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
        Branch = (& git -C $RepositoryRoot branch --show-current).Trim()
    }
}

function Get-McaSingleRawRunDirectory {
    param([string] $EvidenceRoot)
    $runDirectories = @(Get-ChildItem -LiteralPath $EvidenceRoot -Directory)
    if ($runDirectories.Count -ne 1) { Throw-McaFailure 'raw-run-directory-count' 'evidence-root' }
    return $runDirectories[0].FullName
}

function Assert-McaExactKeys {
    param([hashtable] $Record, [string[]] $Expected, [string] $Location)
    $actual = @($Record.Keys | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (Compare-Object $actual $wanted) { Throw-McaFailure 'unsupported-schema' $Location }
}

function Read-McaNdjson {
    param([string] $Path, [string[]] $ExpectedKeys, [string] $ExpectedRecordType)
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt $script:McaFileLimit) { Throw-McaFailure 'file-size-limit' $file.Name }
    $records = [System.Collections.Generic.List[hashtable]]::new()
    $lineNumber = 0
    $reader = [IO.StreamReader]::new($file.FullName, [Text.UTF8Encoding]::new($false, $true), $true)
    try {
        while (($line = $reader.ReadLine()) -ne $null) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line)) { Throw-McaFailure 'blank-ndjson-record' "$($file.Name):$lineNumber" }
            if ([Text.Encoding]::UTF8.GetByteCount($line) -gt $script:McaRecordLimit) {
                Throw-McaFailure 'record-size-limit' "$($file.Name):$lineNumber"
            }
            try { $record = $line | ConvertFrom-Json -AsHashtable -Depth 30 }
            catch { Throw-McaFailure 'malformed-json' "$($file.Name):$lineNumber" }
            if ($record -isnot [hashtable]) { Throw-McaFailure 'json-object-required' "$($file.Name):$lineNumber" }
            Assert-McaExactKeys $record $ExpectedKeys "$($file.Name):$lineNumber"
            if ($ExpectedRecordType -and $record.RecordType -cne $ExpectedRecordType) {
                Throw-McaFailure 'unsupported-record-type' "$($file.Name):$lineNumber"
            }
            $records.Add($record)
        }
    }
    finally { $reader.Dispose() }
    if ($records.Count -eq 0) { Throw-McaFailure 'empty-record-set' $file.Name }
    return $records.ToArray()
}

function Assert-McaForbiddenContent {
    param([string] $Root)
    $patterns = @(
        '(?i)authorization\s*[:=]', '(?i)bearer\s+[a-z0-9._~+/=-]+', '(?i)sharedaccesssignature',
        '(?i)(accountkey|sharedaccesskey(?:name)?|client_secret|access_token|refresh_token|password)\s*[:=]',
        '(?i)(^|[?&])sig=', '(?i)"(?:body|payload|diagnosisjson|repairjson|rediskey)"\s*:',
        '(?i)\b(?:payloadbody|diagnosisjson|repairjson|redis[_-]?key)\s*[:=]'
    )
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadLines($file.FullName)) {
            $lineNumber++
            foreach ($pattern in $patterns) {
                if ($line -match $pattern) { Throw-McaFailure 'forbidden-content' "$($file.Name):$lineNumber" }
            }
        }
    }
}

function Write-McaJson {
    param([string] $Path, [object] $Value)
    $json = $Value | ConvertTo-Json -Depth 30
    $normalizedJson = $json.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText($Path, "$normalizedJson`n", [Text.UTF8Encoding]::new($false))
}

function Test-McaSequenceEqual {
    param([object[]] $Actual, [object[]] $Expected)
    return ($Actual.Count -eq $Expected.Count -and -not (Compare-Object $Actual $Expected -SyncWindow 0))
}

function Publish-McaPacket {
    param(
        [Parameter(Mandatory)][string] $RawRunDirectory,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][hashtable] $Execution
    )
    $destinationPath = if ([IO.Path]::IsPathRooted($Destination)) {
        [IO.Path]::GetFullPath($Destination)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path (Get-Location) $Destination))
    }
    if (Test-Path -LiteralPath $destinationPath) { Throw-McaFailure 'output-already-exists' $destinationPath }
    $parent = Split-Path -Parent $destinationPath
    if (-not $parent) { Throw-McaFailure 'output-parent-required' $destinationPath }
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $staging = Join-Path $parent ('.' + [IO.Path]::GetFileName($destinationPath) + '.mca-staging-' + [guid]::NewGuid().ToString('N'))
    $stagingOwned = $false
    try {
        $actualRawFiles = @(Get-ChildItem -LiteralPath $RawRunDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
        if (Compare-Object $actualRawFiles @($script:McaRawFileNames | Sort-Object)) {
            Throw-McaFailure 'raw-file-set' (Split-Path -Leaf $RawRunDirectory)
        }
        foreach ($name in $script:McaRawFileNames) {
            Assert-McaFileSize (Join-Path $RawRunDirectory $name)
        }

        $recordedHashes = Get-Content -LiteralPath (Join-Path $RawRunDirectory 'artifact-hashes.json') -Raw |
            ConvertFrom-Json -AsHashtable -Depth 10
        if ($recordedHashes -isnot [object[]] -or $recordedHashes.Count -ne 4) {
            Throw-McaFailure 'raw-hash-schema' 'artifact-hashes.json'
        }
        $hashNames = @('run.ndjson', 'samples.ndjson', 'assertions.ndjson', 'summaries.ndjson')
        for ($index = 0; $index -lt $hashNames.Count; $index++) {
            $entry = $recordedHashes[$index]
            Assert-McaExactKeys $entry @('FileName', 'Sha256') "artifact-hashes.json:$($index + 1)"
            $name = $hashNames[$index]
            if ($entry.FileName -cne $name -or $entry.Sha256 -cne (Get-McaSha256 (Join-Path $RawRunDirectory $name))) {
                Throw-McaFailure 'raw-hash-mismatch' $name
            }
        }

        $runs = @(Read-McaNdjson (Join-Path $RawRunDirectory 'run.ndjson') @(
            'CommitSha', 'Branch', 'Dirty', 'Command', 'DotnetVersion', 'DockerVersion', 'ScenarioIds'
        ) $null)
        $samples = @(Read-McaNdjson (Join-Path $RawRunDirectory 'samples.ndjson') @(
            'RecordType', 'RunId', 'ScenarioId', 'SampleId', 'Name', 'Value', 'Labels', 'ObservedAt'
        ) 'MeasurementSample')
        $assertions = @(Read-McaNdjson (Join-Path $RawRunDirectory 'assertions.ndjson') @(
            'RecordType', 'RunId', 'ScenarioId', 'AssertionId', 'Passed', 'Detail', 'ObservedAt'
        ) 'MeasurementAssertion')
        $summaries = @(Read-McaNdjson (Join-Path $RawRunDirectory 'summaries.ndjson') @(
            'RecordType', 'RunId', 'ScenarioId', 'Outcome', 'RequiredAssertionIds', 'RequiredAssertionCount',
            'PassedAssertionCount', 'FailedAssertionIds', 'RuntimeFailures', 'CompletedAt'
        ) 'ScenarioSummary')

        if ($runs.Count -ne 1) { Throw-McaFailure 'single-run-record-required' 'run.ndjson' }
        $run = $runs[0]
        if ($run.CommitSha -cne $Execution.SourceGitSha -or $run.Branch -cne $Execution.SourceGitBranch -or
            $run.Dirty -ne $false -or -not (Test-McaSequenceEqual @($run.ScenarioIds) $script:McaScenarioIds)) {
            Throw-McaFailure 'stale-or-invalid-source-run' 'run.ndjson:1'
        }
        if ($assertions.Count -ne $script:McaRequiredAssertionIds.Count) {
            Throw-McaFailure 'assertion-count' 'assertions.ndjson'
        }
        $actualAssertionIds = @($assertions | ForEach-Object { $_.AssertionId })
        if (($actualAssertionIds | Sort-Object -Unique).Count -ne $actualAssertionIds.Count -or
            (Compare-Object @($actualAssertionIds | Sort-Object) @($script:McaRequiredAssertionIds | Sort-Object))) {
            Throw-McaFailure 'assertion-id-closure' 'assertions.ndjson'
        }
        foreach ($assertion in $assertions) {
            if ($assertion.Passed -ne $true -or $script:McaGroupedScenarioIds -cnotcontains $assertion.ScenarioId) {
                Throw-McaFailure 'failed-or-unknown-assertion' $assertion.AssertionId
            }
        }
        if ($summaries.Count -ne 9 -or -not (Test-McaSequenceEqual @($summaries.ScenarioId) $script:McaGroupedScenarioIds)) {
            Throw-McaFailure 'summary-order' 'summaries.ndjson'
        }
        foreach ($summary in $summaries) {
            $expectedIds = @($script:McaRequiredAssertionIds | Where-Object { $_.StartsWith("$($summary.ScenarioId).", [StringComparison]::Ordinal) })
            $observed = @($assertions | Where-Object ScenarioId -CEQ $summary.ScenarioId)
            if ($summary.Outcome -cne 'Passed' -or $summary.RequiredAssertionCount -ne $expectedIds.Count -or
                $summary.PassedAssertionCount -ne $expectedIds.Count -or @($summary.FailedAssertionIds).Count -ne 0 -or
                @($summary.RuntimeFailures).Count -ne 0 -or
                (Compare-Object @($summary.RequiredAssertionIds | Sort-Object) @($expectedIds | Sort-Object)) -or
                $observed.Count -ne $expectedIds.Count) {
                Throw-McaFailure 'summary-derivation' $summary.ScenarioId
            }
        }
        if ($samples.Count -ne 52) { Throw-McaFailure 'sample-count' 'samples.ndjson' }
        foreach ($sample in $samples) {
            if ($script:McaScenarioIds -cnotcontains $sample.ScenarioId -or [string]::IsNullOrWhiteSpace($sample.SampleId) -or
                [string]::IsNullOrWhiteSpace($sample.Name)) { Throw-McaFailure 'sample-schema' $sample.SampleId }
        }
        foreach ($scenario in $script:McaScenarioIds) {
            foreach ($observation in @('baseline', 'terminal')) {
                foreach ($metric in @('grace_manifest_contribution_messages_total', 'grace_manifest_contribution_processing_duration_milliseconds_count')) {
                    $matches = @($samples | Where-Object {
                        $_.ScenarioId -ceq $scenario -and $_.Name -ceq $metric -and
                        $_.Labels.stage -ceq 'settle' -and $_.Labels.outcome -ceq 'completed' -and
                        $_.Labels.phase -ceq 'stimulus' -and $_.Labels.observation -ceq $observation
                    })
                    if ($matches.Count -ne 1) { Throw-McaFailure 'raw-phase-snapshot' "$scenario/$observation/$metric" }
                }
            }
        }

        Assert-McaForbiddenContent $RawRunDirectory
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        $stagingOwned = $true
        [IO.Directory]::CreateDirectory((Join-Path $staging 'raw')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $staging 'logs')) | Out-Null
        foreach ($name in $script:McaRawFileNames) {
            [IO.File]::Copy((Join-Path $RawRunDirectory $name), (Join-Path $staging "raw/$name"), $false)
        }
        [IO.File]::Copy((Join-Path $RawRunDirectory 'samples.ndjson'), (Join-Path $staging 'samples.ndjson'), $false)

        $rawHashes = [ordered]@{}
        foreach ($name in $script:McaRawFileNames) { $rawHashes[$name] = Get-McaSha256 (Join-Path $RawRunDirectory $name) }
        $runJson = [ordered]@{
            schemaVersion = $script:McaSchemaVersion; sourceGitSha = $Execution.SourceGitSha
            sourceGitBranch = $Execution.SourceGitBranch; sourceDirty = $false; command = $Execution.Command
            outputDirectory = $destinationPath; startedAtUtc = $Execution.StartedAtUtc; finishedAtUtc = $Execution.FinishedAtUtc
            scenarioIds = $script:McaScenarioIds; dotnetVersion = $run.DotnetVersion; dockerVersion = $run.DockerVersion
            machine = $Execution.Machine; os = $Execution.Os; cpuCount = $Execution.CpuCount
            memoryBytes = $Execution.MemoryBytes; rawArtifactHashes = $rawHashes
            localClaims = @(
                'foreground call count and local latency distribution', 'relationship writes and convergence outcomes',
                'worker throughput and backlog drain time', 'serialized actor-document sizes exposed by the fixture',
                'direct exact-relationship and measurement-query Cosmos request charge when exposed',
                'local operation concentration', 'Redis reconnect observations', 'ContentBlock sharing results',
                'emulator broker delivery and dead-letter behavior', 'deterministic correctness assertions'
            )
            azureOnlyUnknowns = @(
                'complete Orleans actor-persistence request charge', 'Azure partition heat, throttling, failover, and availability',
                'cross-region behavior', 'production SLO thresholds', 'HA/DR guarantees',
                'long-handler lock renewal outside the deterministic fixture'
            )
        }
        Write-McaJson (Join-Path $staging 'run.json') $runJson
        Write-McaJson (Join-Path $staging 'assertions.json') $assertions

        $byScenario = foreach ($scenario in $script:McaGroupedScenarioIds) {
            $scenarioAssertions = @($assertions | Where-Object ScenarioId -CEQ $scenario)
            $scenarioSamples = @($samples | Where-Object ScenarioId -CEQ $scenario)
            [ordered]@{ scenarioId = $scenario; outcome = 'Passed'; assertionCount = $scenarioAssertions.Count; sampleCount = $scenarioSamples.Count }
        }
        $aggregates = foreach ($group in $samples | Group-Object ScenarioId, Name | Sort-Object Name) {
            $values = @($group.Group | ForEach-Object { [long]$_.Value })
            [ordered]@{
                scenarioId = $group.Group[0].ScenarioId; name = $group.Group[0].Name; source = 'raw/samples.ndjson'
                derivation = 'count/minimum/maximum/sum over exact scenarioId and name'; count = $values.Count
                minimum = ($values | Measure-Object -Minimum).Minimum; maximum = ($values | Measure-Object -Maximum).Maximum
                sum = ($values | Measure-Object -Sum).Sum
            }
        }
        $summaryJson = [ordered]@{
            schemaVersion = $script:McaSchemaVersion; scenarioOutcomes = $byScenario[0..7]; groupedOutcome = $byScenario[8]
            assertionTotals = [ordered]@{ source = 'raw/assertions.ndjson'; total = $assertions.Count; passed = @($assertions | Where-Object Passed).Count; failed = 0 }
            sampleTotals = [ordered]@{ source = 'raw/samples.ndjson'; total = $samples.Count; phaseSnapshots = 32 }
            numericAggregates = @($aggregates)
        }
        Write-McaJson (Join-Path $staging 'summary.json') $summaryJson

        foreach ($scenario in $script:McaScenarioIds) {
            $lines = [Collections.Generic.List[string]]::new()
            $lineNumber = 0
            foreach ($sample in $samples) { $lineNumber++; if ($sample.ScenarioId -ceq $scenario) { $lines.Add(([ordered]@{ source = 'samples.ndjson'; line = $lineNumber; record = $sample } | ConvertTo-Json -Compress -Depth 30)) } }
            $lineNumber = 0
            foreach ($assertion in $assertions) { $lineNumber++; if ($assertion.ScenarioId -ceq $scenario) { $lines.Add(([ordered]@{ source = 'assertions.ndjson'; line = $lineNumber; record = $assertion } | ConvertTo-Json -Compress -Depth 30)) } }
            $lineNumber = 0
            foreach ($summary in $summaries) { $lineNumber++; if ($summary.ScenarioId -ceq $scenario) { $lines.Add(([ordered]@{ source = 'summaries.ndjson'; line = $lineNumber; record = $summary } | ConvertTo-Json -Compress -Depth 30)) } }
            [IO.File]::WriteAllText(
                (Join-Path $staging "logs/$scenario.jsonl"),
                (($lines -join "`n") + "`n"),
                [Text.UTF8Encoding]::new($false)
            )
        }

        foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File) {
            if ($file.Length -gt $script:McaFileLimit) { Throw-McaFailure 'file-size-limit' $file.Name }
            if ($file.Extension -in @('.json', '.jsonl', '.ndjson')) {
                $lineNumber = 0
                foreach ($line in [IO.File]::ReadLines($file.FullName)) {
                    $lineNumber++
                    if ($file.Extension -ne '.json' -and [string]::IsNullOrWhiteSpace($line)) { Throw-McaFailure 'blank-published-record' "$($file.Name):$lineNumber" }
                    if ([Text.Encoding]::UTF8.GetByteCount($line) -gt $script:McaRecordLimit) { Throw-McaFailure 'record-size-limit' "$($file.Name):$lineNumber" }
                }
            }
        }
        Assert-McaForbiddenContent $staging
        $hashEntries = foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($staging, $file.FullName).Replace('\', '/')
            [ordered]@{ path = $relative; sha256 = Get-McaSha256 $file.FullName }
        }
        Write-McaJson (Join-Path $staging 'artifact-hashes.json') @($hashEntries)
        Assert-McaPublishedHashes $staging
        Assert-McaPacketSize $staging
        if (Test-Path -LiteralPath $destinationPath) { Throw-McaFailure 'output-race' $destinationPath }
        [IO.Directory]::Move($staging, $destinationPath)
        $stagingOwned = $false
        return $destinationPath
    }
    finally {
        if ($stagingOwned -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
}

function Invoke-McaCommand {
    param([string] $RequestedOutput)
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $destination = if ([IO.Path]::IsPathRooted($RequestedOutput)) { [IO.Path]::GetFullPath($RequestedOutput) } else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $RequestedOutput)) }
    if (Test-Path -LiteralPath $destination) { Throw-McaFailure 'output-already-exists' $destination }
    Assert-McaTools @('git', 'dotnet', 'docker')
    $source = Get-McaCleanSource $repositoryRoot
    $sha = $source.Sha
    $branch = $source.Branch
    $dotnetVersion = (& dotnet --version).Trim()
    $dockerVersion = (& docker version --format '{{.Server.Version}}').Trim()
    if ($LASTEXITCODE -ne 0) { Throw-McaFailure 'docker-unavailable' 'docker-version' }
    $started = [DateTimeOffset]::UtcNow.ToString('O')
    $canonicalCommand = 'pwsh ./scripts/measure-manifest-contribution-accounting.ps1 -OutputDirectory ./artifacts/manifest-accounting-measurements'
    $hostedCommand = 'dotnet test src/Grace.Server.Tests/Grace.Server.Tests.fsproj --configuration Release --no-build --filter FullyQualifiedName~ManifestContributionGroupedMeasurementTests'
    $evidenceRoot = Join-Path ([IO.Path]::GetTempPath()) ('grace-mca-752-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
    try {
        & dotnet build (Join-Path $repositoryRoot 'src/Grace.Server.Tests/Grace.Server.Tests.fsproj') --configuration Release
        if ($LASTEXITCODE -ne 0) { Throw-McaFailure 'release-build-failed' 'Grace.Server.Tests' }
        $prior = @{}
        foreach ($name in @('GRACE_MCA_WORKTREE', 'GRACE_MCA_HOSTED_COMMAND', 'GRACE_MCA_EVIDENCE_ROOT', 'GRACE_MCA_EXPECTED_SHA')) { $prior[$name] = [Environment]::GetEnvironmentVariable($name) }
        try {
            $env:GRACE_MCA_WORKTREE = $repositoryRoot; $env:GRACE_MCA_HOSTED_COMMAND = $hostedCommand
            $env:GRACE_MCA_EVIDENCE_ROOT = $evidenceRoot; $env:GRACE_MCA_EXPECTED_SHA = $sha
            & dotnet test (Join-Path $repositoryRoot 'src/Grace.Server.Tests/Grace.Server.Tests.fsproj') --configuration Release --no-build --filter 'FullyQualifiedName~ManifestContributionGroupedMeasurementTests'
            if ($LASTEXITCODE -ne 0) { Throw-McaFailure 'grouped-runtime-failed' 'ManifestContributionGroupedMeasurementTests' }
        }
        finally { foreach ($name in $prior.Keys) { [Environment]::SetEnvironmentVariable($name, $prior[$name]) } }
        $rawRunDirectory = Get-McaSingleRawRunDirectory $evidenceRoot
        $execution = @{
            SourceGitSha = $sha; SourceGitBranch = $branch; Command = $canonicalCommand; StartedAtUtc = $started
            FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); Machine = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { 'unavailable' }
            Os = [Environment]::OSVersion.VersionString; CpuCount = [Environment]::ProcessorCount
            MemoryBytes = [GC]::GetGCMemoryInfo().TotalAvailableMemoryBytes
        }
        $published = Publish-McaPacket $rawRunDirectory $destination $execution
        Write-Host "Manifest contribution accounting packet: $published"
    }
    finally { if (Test-Path -LiteralPath $evidenceRoot) { Remove-Item -LiteralPath $evidenceRoot -Recurse -Force } }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-McaCommand $OutputDirectory
}
