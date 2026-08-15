Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scriptPath = Join-Path $repositoryRoot 'scripts/measure-manifest-contribution-accounting.ps1'
. $scriptPath -OutputDirectory 'unused-while-dot-sourced'

$script:Passed = 0
$script:Failed = 0
$script:TestRunId = '0123456789abcdef0123456789abcdef'

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
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

function Write-TestNdjson {
    param([string] $Path, [object[]] $Records)
    $lines = @($Records | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 30 })
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

function Update-TestRawHashes {
    param([string] $RawDirectory)
    $entries = foreach ($name in @('run.ndjson', 'samples.ndjson', 'assertions.ndjson', 'summaries.ndjson')) {
        [ordered]@{ FileName = $name; Sha256 = Get-McaSha256 (Join-Path $RawDirectory $name) }
    }
    [IO.File]::WriteAllText(
        (Join-Path $RawDirectory 'artifact-hashes.json'),
        ($entries | ConvertTo-Json -Compress -Depth 10),
        [Text.UTF8Encoding]::new($false)
    )
}

function New-TestRawPacket {
    param([string] $RawDirectory, [string] $Sha = ('a' * 40), [string] $Branch = 'agent/test')
    [IO.Directory]::CreateDirectory($RawDirectory) | Out-Null
    $runId = $script:TestRunId
    $time = '2026-08-01T00:00:00.0000000+00:00'
    $run = [ordered]@{
        CommitSha = $Sha; Branch = $Branch; Dirty = $false; Command = 'grouped fixture command'
        DotnetVersion = '10.0.100'; DockerVersion = '28.0.0'; ScenarioIds = $script:McaScenarioIds
    }
    Write-TestNdjson (Join-Path $RawDirectory 'run.ndjson') @($run)

    $samples = [Collections.Generic.List[object]]::new()
    foreach ($scenario in $script:McaScenarioIds) {
        foreach ($observation in @('baseline', 'terminal')) {
            foreach ($metric in @('grace_manifest_contribution_messages_total', 'grace_manifest_contribution_processing_duration_milliseconds_count')) {
                $samples.Add([ordered]@{
                    RecordType = 'MeasurementSample'; RunId = $runId; ScenarioId = $scenario
                    SampleId = "$scenario-$observation-$($samples.Count)"; Name = $metric; Value = [long]$samples.Count
                    Labels = [ordered]@{ stage = 'settle'; outcome = 'completed'; phase = 'stimulus'; observation = $observation }
                    ObservedAt = $time
                })
            }
        }
    }
    for ($index = 0; $index -lt 20; $index++) {
        $scenario = $script:McaScenarioIds[$index % $script:McaScenarioIds.Count]
        $samples.Add([ordered]@{
            RecordType = 'MeasurementSample'; RunId = $runId; ScenarioId = $scenario; SampleId = "extra-$index"
            Name = 'fixture.numeric-observation'; Value = [long]($index + 1); Labels = [ordered]@{ kind = 'derived-input' }
            ObservedAt = $time
        })
    }
    Write-TestNdjson (Join-Path $RawDirectory 'samples.ndjson') $samples.ToArray()

    $assertions = foreach ($assertionId in $script:McaRequiredAssertionIds) {
        [ordered]@{
            RecordType = 'MeasurementAssertion'; RunId = $runId; ScenarioId = $assertionId.Split('.')[0]
            AssertionId = $assertionId; Passed = $true; Detail = 'synthetic accepted witness'; ObservedAt = $time
        }
    }
    Write-TestNdjson (Join-Path $RawDirectory 'assertions.ndjson') @($assertions)

    $summaries = foreach ($scenario in $script:McaGroupedScenarioIds) {
        $ids = @($script:McaRequiredAssertionIds | Where-Object { $_.StartsWith("$scenario.", [StringComparison]::Ordinal) })
        [ordered]@{
            RecordType = 'ScenarioSummary'; RunId = $runId; ScenarioId = $scenario; Outcome = 'Passed'
            RequiredAssertionIds = $ids; RequiredAssertionCount = $ids.Count; PassedAssertionCount = $ids.Count
            FailedAssertionIds = @(); RuntimeFailures = @(); CompletedAt = $time
        }
    }
    Write-TestNdjson (Join-Path $RawDirectory 'summaries.ndjson') @($summaries)
    Update-TestRawHashes $RawDirectory
}

function New-TestExecution {
    param(
        [string] $Sha = ('a' * 40),
        [string] $Branch = 'agent/test',
        [string] $RequestedOutput = './artifacts/manifest-accounting-measurements'
    )
    $sourceSha = $Sha
    $sourceBranch = $Branch
    $sourceStateReader = {
        param([string] $RepositoryRoot)
        return @{ Sha = $sourceSha; Branch = $sourceBranch; Status = @() }
    }.GetNewClosure()
    return @{
        RepositoryRoot = 'synthetic-test-repository'; SourceGitSha = $Sha; SourceGitBranch = $Branch
        _SourceStateReader = $sourceStateReader
        Command = Get-McaInvocationCommand $RequestedOutput
        StartedAtUtc = '2026-08-01T00:00:00Z'; FinishedAtUtc = '2026-08-01T00:10:00Z'
        Machine = 'test-machine'; Os = 'test-os'; CpuCount = 4; MemoryBytes = 8589934592
    }
}

function Copy-TestRawPacket {
    param([string] $Source, [string] $Destination)
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $copy = Join-Path $Destination (Split-Path -Leaf $Source)
    Copy-Item -LiteralPath $Source -Destination $copy -Recurse
    return $copy
}

function Invoke-ExpectedPublicationFailure {
    param([string] $Raw, [string] $Output, [scriptblock] $Check = {})
    $failed = $false
    try { Publish-McaPacket $Raw $Output (New-TestExecution) | Out-Null }
    catch { $failed = $true; & $Check $_ }
    Assert-True $failed 'publication should fail'
    Assert-True (-not (Test-Path -LiteralPath $Output)) 'failed publication must leave the target absent'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('grace-mca-752-script-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $validRaw = Join-Path $testRoot $script:TestRunId
    New-TestRawPacket $validRaw

    Invoke-Case 'positive absolute publication and exact tree' {
        $output = Join-Path $testRoot 'absolute-output'
        $execution = New-TestExecution -RequestedOutput $output
        $published = Publish-McaPacket $validRaw $output $execution
        Assert-True ($published -ceq [IO.Path]::GetFullPath($output)) 'absolute output should resolve exactly'
        $expected = @(
            'artifact-hashes.json', 'assertions.json', 'run.json', 'samples.ndjson', 'summary.json',
            'raw/artifact-hashes.json', 'raw/assertions.ndjson', 'raw/run.ndjson', 'raw/samples.ndjson', 'raw/summaries.ndjson'
        ) + @($script:McaScenarioIds | ForEach-Object { "logs/$_.jsonl" })
        $actual = @(Get-ChildItem $output -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/') } | Sort-Object)
        Assert-True (-not (Compare-Object @($expected | Sort-Object) $actual)) 'published tree should be exact'
        foreach ($name in $script:McaRawFileNames) {
            Assert-True ((Get-McaSha256 (Join-Path $validRaw $name)) -ceq (Get-McaSha256 (Join-Path $output "raw/$name"))) "raw $name bytes should be retained"
        }
        $hashes = Get-Content (Join-Path $output 'artifact-hashes.json') -Raw | ConvertFrom-Json
        Assert-True ($hashes.Count -eq 17) 'root hash manifest should hash every other file'
        Assert-True (@($hashes | Where-Object path -CEQ 'artifact-hashes.json').Count -eq 0) 'root hash manifest must not hash itself'
        foreach ($entry in $hashes) {
            Assert-True ($entry.sha256 -cmatch '^[0-9a-f]{64}$') 'published hashes should be lowercase SHA-256'
            Assert-True ($entry.sha256 -ceq (Get-McaSha256 (Join-Path $output $entry.path))) "hash should verify for $($entry.path)"
        }
        foreach ($file in @(Get-ChildItem $output -File -Filter '*.json') + @(Get-ChildItem (Join-Path $output 'logs') -File -Filter '*.jsonl')) {
            Assert-True (-not [IO.File]::ReadAllText($file.FullName).Contains("`r")) "$($file.Name) should use LF-only generated lines"
        }
        $hashPaths = @($hashes.path)
        Assert-True (-not (Compare-Object $hashPaths @($hashPaths | Sort-Object) -SyncWindow 0)) 'hash paths should be deterministic'
        $publishedRun = Get-Content (Join-Path $output 'run.json') -Raw | ConvertFrom-Json
        Assert-True ($publishedRun.command -ceq (Get-McaInvocationCommand $output)) 'command should record the actual absolute invocation'
        Assert-True ($publishedRun.outputDirectory -ceq [IO.Path]::GetFullPath($output)) 'run output should record the resolved absolute destination'
        Assert-True ($publishedRun.localClaims.Count -eq 10) 'local claim set should be explicit and exact'
        Assert-True ($publishedRun.azureOnlyUnknowns.Count -eq 6) 'Azure-only unknown set should be explicit and exact'
        Assert-True ($publishedRun.localClaims -notcontains $publishedRun.azureOnlyUnknowns[0]) 'local and Azure claims must not overlap'
    }

    Invoke-Case 'positive relative publication' {
        Push-Location $testRoot
        try {
            $execution = New-TestExecution -RequestedOutput './relative-output'
            $published = Publish-McaPacket $validRaw './relative-output' $execution
            Assert-True ($published -ceq [IO.Path]::GetFullPath((Join-Path $testRoot 'relative-output'))) 'relative output should resolve from the caller location'
            $publishedRun = Get-Content (Join-Path $published 'run.json') -Raw | ConvertFrom-Json
            Assert-True ($publishedRun.command -ceq (Get-McaInvocationCommand './relative-output')) 'command should record the actual relative invocation'
            Assert-True ($publishedRun.outputDirectory -ceq $published) 'relative run output should record the resolved destination'
        }
        finally { Pop-Location }
    }

    Invoke-Case 'invocation safely escapes a PowerShell path literal' {
        $requested = "C:\packet'; Write-Host injected; 'tail"
        $command = Get-McaInvocationCommand $requested
        Assert-True ($command -ceq "pwsh ./scripts/measure-manifest-contribution-accounting.ps1 -OutputDirectory 'C:\packet''; Write-Host injected; ''tail'") 'single quotes should be escaped deterministically'
        $tokens = $null; $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseInput($command, [ref]$tokens, [ref]$parseErrors) | Out-Null
        Assert-True ($parseErrors.Count -eq 0) 'escaped invocation should remain parseable'
        Assert-True (@($tokens | Where-Object Kind -EQ 'Semi').Count -eq 0) 'path punctuation must not create a command separator'
    }

    Invoke-Case 'unknown parameters fail in binding before runtime' {
        $target = Join-Path $testRoot 'unknown-parameter-output'
        & pwsh -NoProfile -File $scriptPath -OutputDirectory $target -Scenario baseline *> $null
        Assert-True ($LASTEXITCODE -ne 0) 'unknown Scenario parameter should fail'
        Assert-True (-not (Test-Path $target)) 'unknown parameter must not start publication'
    }

    Invoke-Case 'existing output is never overwritten' {
        $output = Join-Path $testRoot 'existing-output'; [IO.Directory]::CreateDirectory($output) | Out-Null
        [IO.File]::WriteAllText((Join-Path $output 'owner.txt'), 'retain')
        $failed = $false; try { Publish-McaPacket $validRaw $output (New-TestExecution) | Out-Null } catch { $failed = $true }
        Assert-True $failed 'existing output should fail'; Assert-True ((Get-Content (Join-Path $output 'owner.txt')) -ceq 'retain') 'existing content must remain'
    }

    Invoke-Case 'missing tool is rejected' {
        $oldPath = $env:PATH; try { $env:PATH = $testRoot; $failed = $false; try { Assert-McaTools @('tool-that-does-not-exist') } catch { $failed = $true }; Assert-True $failed 'missing tool should fail' } finally { $env:PATH = $oldPath }
    }

    Invoke-Case 'dirty source worktree is rejected' {
        $repo = Join-Path $testRoot 'dirty-repo'; [IO.Directory]::CreateDirectory($repo) | Out-Null
        & git -C $repo init -q; & git -C $repo config user.email test@example.invalid; & git -C $repo config user.name Test
        [IO.File]::WriteAllText((Join-Path $repo 'tracked.txt'), 'clean'); & git -C $repo add tracked.txt; & git -C $repo commit -q -m initial
        [IO.File]::WriteAllText((Join-Path $repo 'untracked.txt'), 'dirty')
        $failed = $false; try { Get-McaCleanSource $repo | Out-Null } catch { $failed = $true }; Assert-True $failed 'dirty repo should fail'
    }

    Invoke-Case 'zero and multiple raw directories are rejected' {
        $root = Join-Path $testRoot 'run-count'; [IO.Directory]::CreateDirectory($root) | Out-Null
        $failed = $false; try { Get-McaSingleRawRunDirectory $root | Out-Null } catch { $failed = $true }; Assert-True $failed 'zero runs should fail'
        [IO.Directory]::CreateDirectory((Join-Path $root 'one')) | Out-Null; [IO.Directory]::CreateDirectory((Join-Path $root 'two')) | Out-Null
        $failed = $false; try { Get-McaSingleRawRunDirectory $root | Out-Null } catch { $failed = $true }; Assert-True $failed 'multiple runs should fail'
    }

    $negativeCases = @(
        @{ Name = 'missing raw file'; Mutate = { param($raw) Remove-Item (Join-Path $raw 'samples.ndjson') }; Rehash = $false },
        @{ Name = 'extra raw file'; Mutate = { param($raw) [IO.File]::WriteAllText((Join-Path $raw 'extra.json'), '{}') }; Rehash = $false },
        @{ Name = 'malformed record'; Mutate = { param($raw) [IO.File]::WriteAllText((Join-Path $raw 'run.ndjson'), '{') }; Rehash = $true },
        @{ Name = 'blank NDJSON line'; Mutate = { param($raw) [IO.File]::AppendAllText((Join-Path $raw 'samples.ndjson'), "`n") }; Rehash = $true },
        @{ Name = 'raw hash mismatch'; Mutate = { param($raw) [IO.File]::AppendAllText((Join-Path $raw 'samples.ndjson'), ' ') }; Rehash = $false },
        @{ Name = 'malformed raw hash manifest'; Mutate = { param($raw) [IO.File]::WriteAllText((Join-Path $raw 'artifact-hashes.json'), '{') }; Rehash = $false },
        @{ Name = 'empty scenario plan'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'run.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].ScenarioIds=@();Write-TestNdjson (Join-Path $raw 'run.ndjson') $r }; Rehash = $true },
        @{ Name = 'failed assertion'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'assertions.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].Passed=$false;Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $r }; Rehash = $true },
        @{ Name = 'unknown assertion'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'assertions.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].AssertionId='baseline.unknown';Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $r }; Rehash = $true },
        @{ Name = 'duplicate assertion'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'assertions.ndjson')|ConvertFrom-Json -AsHashtable);$r[1].AssertionId=$r[0].AssertionId;Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $r }; Rehash = $true },
        @{ Name = 'failed summary'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'summaries.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].Outcome='Failed';Write-TestNdjson (Join-Path $raw 'summaries.ndjson') $r }; Rehash = $true },
        @{ Name = 'skipped summary'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'summaries.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].Outcome='Skipped';Write-TestNdjson (Join-Path $raw 'summaries.ndjson') $r }; Rehash = $true },
        @{ Name = 'unsupported plausible summary'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'summaries.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].UnsupportedTotal=11;Write-TestNdjson (Join-Path $raw 'summaries.ndjson') $r }; Rehash = $true },
        @{ Name = 'summary derivation mismatch'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'summaries.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].PassedAssertionCount=10;Write-TestNdjson (Join-Path $raw 'summaries.ndjson') $r }; Rehash = $true },
        @{ Name = 'empty required assertion array'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'summaries.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].RequiredAssertionIds=@();$r[0].RequiredAssertionCount=0;$r[0].PassedAssertionCount=0;Write-TestNdjson (Join-Path $raw 'summaries.ndjson') $r }; Rehash = $true },
        @{ Name = 'missing phase snapshot'; Mutate = { param($raw) $r=@(Get-Content (Join-Path $raw 'samples.ndjson')|ConvertFrom-Json -AsHashtable);$r[0].Labels.observation='changed';Write-TestNdjson (Join-Path $raw 'samples.ndjson') $r }; Rehash = $true }
    )
    foreach ($case in $negativeCases) {
        Invoke-Case $case.Name {
            $slug = $case.Name.Replace(' ', '-'); $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot "raw-$slug")
            & $case.Mutate $raw; if ($case.Rehash -and (Test-Path (Join-Path $raw 'samples.ndjson'))) { Update-TestRawHashes $raw }
            Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot "out-$slug")
        }
    }

    Invoke-Case 'duplicate required summary assertion ID is rejected by cardinality' {
        $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot 'raw-duplicate-summary-required-id')
        $records = @(Get-Content (Join-Path $raw 'summaries.ndjson') | ConvertFrom-Json -AsHashtable)
        $records[0].RequiredAssertionIds = @($records[0].RequiredAssertionIds) + $records[0].RequiredAssertionIds[0]
        Write-TestNdjson (Join-Path $raw 'summaries.ndjson') $records; Update-TestRawHashes $raw
        Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot 'out-duplicate-summary-required-id') {
            param($errorRecord)
            Assert-True ($errorRecord.Exception.Message.Contains('summary-required-id-cardinality')) 'duplicate summary ID should fail the explicit cardinality rule'
        }
    }

    Invoke-Case 'known assertions cannot swap exact scenarios' {
        $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot 'raw-swapped-scenarios')
        $records = @(Get-Content (Join-Path $raw 'assertions.ndjson') | ConvertFrom-Json -AsHashtable)
        foreach ($record in $records) {
            if ($record.ScenarioId -ceq 'baseline') { $record.ScenarioId = 'hot-manifest' }
            elseif ($record.ScenarioId -ceq 'hot-manifest') { $record.ScenarioId = 'baseline' }
        }
        Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $records; Update-TestRawHashes $raw
        Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot 'out-swapped-scenarios')
    }

    Invoke-Case 'all raw records must share the directory run ID' {
        $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot 'raw-run-id-mismatch')
        $records = @(Get-Content (Join-Path $raw 'samples.ndjson') | ConvertFrom-Json -AsHashtable)
        $records[0].RunId = 'fedcba9876543210fedcba9876543210'
        Write-TestNdjson (Join-Path $raw 'samples.ndjson') $records; Update-TestRawHashes $raw
        Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot 'out-run-id-mismatch')
    }

    Invoke-Case 'forbidden secret fails without echoing value' {
        $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot 'raw-secret')
        $records = @(Get-Content (Join-Path $raw 'assertions.ndjson') | ConvertFrom-Json -AsHashtable)
        $secret = 'super-secret-never-echo'; $records[0].Detail = "Authorization: Bearer $secret"
        Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $records; Update-TestRawHashes $raw
        Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot 'out-secret') { param($errorRecord) Assert-True (-not $errorRecord.Exception.Message.Contains($secret)) 'diagnostic must not echo secret' }
    }

    foreach ($tokenForm in @('Token: generic-secret-never-echo', 'token=generic-secret-never-echo')) {
        Invoke-Case "generic token form $($tokenForm.Substring(0, 6)) fails without echoing value" {
            $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot ([guid]::NewGuid().ToString('N')))
            $records = @(Get-Content (Join-Path $raw 'assertions.ndjson') | ConvertFrom-Json -AsHashtable)
            $secret = 'generic-secret-never-echo'; $records[0].Detail = $tokenForm
            Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $records; Update-TestRawHashes $raw
            Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot ([guid]::NewGuid().ToString('N'))) {
                param($errorRecord)
                Assert-True (-not $errorRecord.Exception.Message.Contains($secret)) 'generic token diagnostic must not echo secret'
            }
        }
    }

    Invoke-Case 'ordinary token count and type prose is allowed' {
        $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot 'raw-token-prose')
        $records = @(Get-Content (Join-Path $raw 'assertions.ndjson') | ConvertFrom-Json -AsHashtable)
        $records[0].Detail = 'token count is 11; token type is synthetic'
        Write-TestNdjson (Join-Path $raw 'assertions.ndjson') $records; Update-TestRawHashes $raw
        $output = Join-Path $testRoot 'out-token-prose'
        Publish-McaPacket $raw $output (New-TestExecution -RequestedOutput $output) | Out-Null
        Assert-True (Test-Path -LiteralPath $output) 'ordinary token prose should publish'
    }

    Invoke-Case 'wrong and stale source SHA fail' {
        $output = Join-Path $testRoot 'out-stale'; $failed = $false
        try { Publish-McaPacket $validRaw $output (New-TestExecution -Sha ('b' * 40)) | Out-Null } catch { $failed = $true }
        Assert-True $failed 'stale source SHA should fail'; Assert-True (-not (Test-Path $output)) 'stale packet target absent'
    }

    Invoke-Case 'changed source state immediately before publication leaves destination absent' {
        $output = Join-Path $testRoot 'out-changed-source'
        $execution = New-TestExecution -RequestedOutput $output
        $execution._SourceStateReader = { param([string] $RepositoryRoot) return @{ Sha = ('a' * 40); Branch = 'agent/test'; Status = @('?? changed-after-run.txt') } }
        $failed = $false
        try { Publish-McaPacket $validRaw $output $execution | Out-Null }
        catch {
            $failed = $true
            Assert-True ($_.Exception.Message.Contains('source-freshness')) 'changed source should fail the freshness rule'
        }
        Assert-True $failed 'changed source should prevent publication'
        Assert-True (-not (Test-Path -LiteralPath $output)) 'changed source target should remain absent'
    }

    Invoke-Case 'record byte limit and UTF-8 byte counting' {
        $path = Join-Path $testRoot 'record-limit.ndjson'
        $prefix = '{"x":"'; $suffix = '"}'; $fill = 'é' * [int](($script:McaRecordLimit - [Text.Encoding]::UTF8.GetByteCount($prefix + $suffix)) / 2)
        [IO.File]::WriteAllText($path, $prefix + $fill + $suffix, [Text.UTF8Encoding]::new($false))
        $records = @(Read-McaNdjson $path @('x') $null); Assert-True ($records.Count -eq 1) 'exact UTF-8 record limit should pass'
        [IO.File]::AppendAllText($path, 'x', [Text.UTF8Encoding]::new($false))
        $failed = $false; try { Read-McaNdjson $path @('x') $null | Out-Null } catch { $failed = $true }; Assert-True $failed 'one-byte record overflow should fail'
    }

    Invoke-Case 'file limit and one-byte overflow' {
        $boundary = Join-Path $testRoot 'file-boundary.bin'
        [IO.File]::WriteAllBytes($boundary, [byte[]]::new($script:McaFileLimit)); Assert-McaFileSize $boundary
        [IO.File]::WriteAllBytes($boundary, [byte[]]::new($script:McaFileLimit + 1))
        $failed = $false; try { Assert-McaFileSize $boundary } catch { $failed = $true }; Assert-True $failed 'one-byte file overflow should fail'
        $raw = Copy-TestRawPacket $validRaw (Join-Path $testRoot 'raw-file-overflow')
        [IO.File]::WriteAllBytes((Join-Path $raw 'samples.ndjson'), [byte[]]::new($script:McaFileLimit + 1))
        Invoke-ExpectedPublicationFailure $raw (Join-Path $testRoot 'out-file-overflow')
    }

    Invoke-Case 'packet limit and one-byte overflow' {
        $packet = Join-Path $testRoot 'packet-boundary'; [IO.Directory]::CreateDirectory($packet) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $packet 'bytes.bin'), [byte[]]::new($script:McaPacketLimit)); Assert-McaPacketSize $packet
        [IO.File]::WriteAllBytes((Join-Path $packet 'bytes.bin'), [byte[]]::new($script:McaPacketLimit + 1))
        $failed = $false; try { Assert-McaPacketSize $packet } catch { $failed = $true }; Assert-True $failed 'one-byte packet overflow should fail'
    }

    Invoke-Case 'published hash mismatch is rejected' {
        $output = Join-Path $testRoot 'published-hash-output'; Publish-McaPacket $validRaw $output (New-TestExecution) | Out-Null
        [IO.File]::AppendAllText((Join-Path $output 'summary.json'), ' ')
        $failed = $false; try { Assert-McaPublishedHashes $output } catch { $failed = $true }; Assert-True $failed 'published mismatch should fail'
    }

    Invoke-Case 'deterministic projections and hashes are stable' {
        $first = Join-Path $testRoot 'deterministic-first'; $second = Join-Path $testRoot 'deterministic-second'
        Publish-McaPacket $validRaw $first (New-TestExecution) | Out-Null
        Publish-McaPacket $validRaw $second (New-TestExecution) | Out-Null
        $stablePaths = @('assertions.json', 'samples.ndjson', 'summary.json') + @($script:McaRawFileNames | ForEach-Object { "raw/$_" }) + @($script:McaScenarioIds | ForEach-Object { "logs/$_.jsonl" })
        foreach ($path in $stablePaths) { Assert-True ((Get-McaSha256 (Join-Path $first $path)) -ceq (Get-McaSha256 (Join-Path $second $path))) "$path should be deterministic" }
        $firstPaths = @((Get-Content (Join-Path $first 'artifact-hashes.json') -Raw | ConvertFrom-Json).path)
        $secondPaths = @((Get-Content (Join-Path $second 'artifact-hashes.json') -Raw | ConvertFrom-Json).path)
        Assert-True (-not (Compare-Object $firstPaths $secondPaths -SyncWindow 0)) 'hash manifest ordering should be stable'
    }

    Invoke-Case 'atomic destination race cannot overwrite' {
        $output = Join-Path $testRoot 'race-output'; [IO.Directory]::CreateDirectory($output) | Out-Null
        [IO.File]::WriteAllText((Join-Path $output 'attacker.txt'), 'retain')
        $failed = $false; try { Publish-McaPacket $validRaw $output (New-TestExecution) | Out-Null } catch { $failed = $true }
        Assert-True $failed 'race target should fail'; Assert-True ((Get-Content (Join-Path $output 'attacker.txt')) -ceq 'retain') 'race content cannot be overwritten'
    }

    Invoke-Case 'publication write failure leaves target absent and staging removed' {
        $parentFile = Join-Path $testRoot 'not-a-directory'; [IO.File]::WriteAllText($parentFile, 'owner')
        $output = Join-Path $parentFile 'packet'; $failed = $false; try { Publish-McaPacket $validRaw $output (New-TestExecution) | Out-Null } catch { $failed = $true }
        Assert-True $failed 'write failure should be nonzero'; Assert-True (-not (Test-Path $output)) 'write failure target absent'
        Assert-True (@(Get-ChildItem $testRoot -Filter '.packet.mca-staging-*').Count -eq 0) 'script staging should be absent'
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "Result: $script:Passed passed; $script:Failed failed"
if ($script:Failed -ne 0) { exit 1 }
