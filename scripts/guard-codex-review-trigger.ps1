[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int] $PullRequest,

    [Parameter()]
    [ValidatePattern('^[0-9a-fA-F]{7,40}$')]
    [string] $HeadSha,

    [Parameter()]
    [string] $Owner,

    [Parameter()]
    [string] $Name,

    [Parameter()]
    [ValidateRange(0, 1440)]
    [int] $MinimumWaitMinutes = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-ReviewStatusBlock {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CurrentHeadSha,

        [Parameter(Mandatory = $true)]
        [string] $BotState,

        [Parameter(Mandatory = $true)]
        [string] $ManualTriggerDecision,

        [Parameter(Mandatory = $true)]
        [string] $ManualTriggerLock,

        [Parameter(Mandatory = $true)]
        [string] $Details
    )

    Write-Output '## Review Status'
    Write-Output ''
    Write-Output "- Current head SHA: ``$CurrentHeadSha``"
    Write-Output "- Codex Code Review Bot state for current head: $BotState"
    Write-Output "- Manual trigger decision: $ManualTriggerDecision"
    Write-Output "- Manual trigger lock: $ManualTriggerLock"
    Write-Output "- Detailed review/fix comments: $Details"
}

function Invoke-GhJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $output = & gh @Arguments 2>&1
    $displayArguments = $Arguments -join ' '
    if ($Arguments.Count -ge 2 -and $Arguments[0] -eq 'api' -and $Arguments[1] -eq 'graphql') {
        $displayArguments = 'api graphql'
    }

    if ($LASTEXITCODE -ne 0) {
        throw "gh $displayArguments failed: $output"
    }

    if ([string]::IsNullOrWhiteSpace(($output -join "`n"))) {
        throw "gh $displayArguments returned empty output."
    }

    return ($output | ConvertFrom-Json)
}

function Get-RepositoryIdentity {
    param(
        [string] $ExplicitOwner,
        [string] $ExplicitName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitOwner) -and -not [string]::IsNullOrWhiteSpace($ExplicitName)) {
        return [pscustomobject]@{
            Owner = $ExplicitOwner
            Name = $ExplicitName
        }
    }

    $repo = Invoke-GhJson -Arguments @('repo', 'view', '--json', 'owner,name')

    $resolvedOwner = $ExplicitOwner
    if ([string]::IsNullOrWhiteSpace($resolvedOwner)) {
        $resolvedOwner = [string]$repo.owner.login
    }

    $resolvedName = $ExplicitName
    if ([string]::IsNullOrWhiteSpace($resolvedName)) {
        $resolvedName = [string]$repo.name
    }

    if ([string]::IsNullOrWhiteSpace($resolvedOwner) -or [string]::IsNullOrWhiteSpace($resolvedName)) {
        throw 'Could not resolve repository owner/name. Pass -Owner and -Name explicitly.'
    }

    return [pscustomobject]@{
        Owner = $resolvedOwner
        Name = $resolvedName
    }
}

function Test-IsCodexAuthor {
    param(
        [AllowNull()]
        [object] $Author
    )

    if ($null -eq $Author) {
        return $false
    }

    $login = [string]$Author.login
    if ([string]::IsNullOrWhiteSpace($login)) {
        return $false
    }

    return $login -match '(?i)codex|openai'
}

function Test-BodyMentionsHead {
    param(
        [AllowNull()]
        [string] $Body,

        [Parameter(Mandatory = $true)]
        [string] $CurrentHeadSha
    )

    if ([string]::IsNullOrWhiteSpace($Body)) {
        return $false
    }

    return $Body.Contains($CurrentHeadSha) -or $Body.Contains($CurrentHeadSha.Substring(0, 7))
}

function ConvertFrom-GitHubUtcTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Timestamp
    )

    if ($Timestamp -is [DateTimeOffset]) {
        return ([DateTimeOffset]$Timestamp).ToUniversalTime()
    }

    if ($Timestamp -is [DateTime]) {
        $dateTime = [DateTime]$Timestamp
        if ($dateTime.Kind -eq [DateTimeKind]::Unspecified) {
            $dateTime = [DateTime]::SpecifyKind($dateTime, [DateTimeKind]::Utc)
        }

        return ([DateTimeOffset]$dateTime.ToUniversalTime())
    }

    $timestampText = [string]$Timestamp
    if ([string]::IsNullOrWhiteSpace($timestampText)) {
        throw 'GitHub UTC timestamp was empty.'
    }

    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
        [System.Globalization.DateTimeStyles]::AdjustToUniversal
    $formats = [string[]]@(
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"
    )

    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParseExact(
            $timestampText,
            $formats,
            [System.Globalization.CultureInfo]::InvariantCulture,
            $styles,
            [ref]$parsed)) {
        return $parsed.ToUniversalTime()
    }

    throw "Could not parse GitHub UTC timestamp '$timestampText'."
}

function Test-TimestampOnOrAfter {
    param(
        [AllowNull()]
        [object] $Timestamp,

        [Parameter(Mandatory = $true)]
        [DateTimeOffset] $ReferenceTime
    )

    if ($null -eq $Timestamp -or ($Timestamp -is [string] -and [string]::IsNullOrWhiteSpace([string]$Timestamp))) {
        return $false
    }

    return (ConvertFrom-GitHubUtcTimestamp -Timestamp $Timestamp) -ge $ReferenceTime
}

try {
    $repoIdentity = Get-RepositoryIdentity -ExplicitOwner $Owner -ExplicitName $Name

    $query = @'
query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      number
      title
      url
      updatedAt
      headRefOid
      commits(last: 1) { nodes { commit { oid committedDate } } }
      comments(first: 100) { nodes { author { login } body createdAt updatedAt url } }
      reviews(first: 50) {
        nodes {
          author { login }
          state
          body
          submittedAt
          url
          commit { oid }
          comments(first: 100) {
            nodes { author { login } body createdAt updatedAt path line url }
          }
        }
      }
      reactionGroups {
        content
        users(first: 100) { totalCount nodes { login } }
      }
    }
  }
}
'@

    $graphql = Invoke-GhJson -Arguments @(
        'api',
        'graphql',
        '-f',
        "query=$query",
        '-F',
        "owner=$($repoIdentity.Owner)",
        '-F',
        "name=$($repoIdentity.Name)",
        '-F',
        "number=$PullRequest"
    )

    $pr = $graphql.data.repository.pullRequest
    if ($null -eq $pr) {
        throw "Pull request #$PullRequest was not found in $($repoIdentity.Owner)/$($repoIdentity.Name)."
    }

    $currentHeadSha = [string]$pr.headRefOid
    if ([string]::IsNullOrWhiteSpace($currentHeadSha)) {
        throw 'The pull request head SHA could not be resolved.'
    }

    if (-not [string]::IsNullOrWhiteSpace($HeadSha) -and
        -not $currentHeadSha.StartsWith($HeadSha, [StringComparison]::OrdinalIgnoreCase) -and
        -not $HeadSha.StartsWith($currentHeadSha, [StringComparison]::OrdinalIgnoreCase)) {
        Write-ReviewStatusBlock `
            -CurrentHeadSha $currentHeadSha `
            -BotState 'ambiguous: supplied -HeadSha does not match the live pull request head' `
            -ManualTriggerDecision 'blocked; inspect the live PR head before making a review request' `
            -ManualTriggerLock 'active: requested head does not match current head' `
            -Details "PR: $($pr.url)"
        exit 2
    }

    $reactionGroups = @($pr.reactionGroups)
    $eyesReaction = $reactionGroups |
        Where-Object { [string]$_.content -eq 'EYES' -and [int]$_.users.totalCount -gt 0 } |
        Select-Object -First 1
    $thumbsUpReaction = $reactionGroups |
        Where-Object { [string]$_.content -eq 'THUMBS_UP' -and [int]$_.users.totalCount -gt 0 } |
        Select-Object -First 1

    $lastCommit = @($pr.commits.nodes) | Select-Object -Last 1
    $commitTimeText = $null
    if ($null -ne $lastCommit -and $null -ne $lastCommit.commit) {
        $commitTimeText = $lastCommit.commit.committedDate
    }

    if ($null -eq $commitTimeText -or
        ($commitTimeText -is [string] -and [string]::IsNullOrWhiteSpace([string]$commitTimeText))) {
        throw 'The pull request head commit timestamp could not be resolved.'
    }

    $commitTime = ConvertFrom-GitHubUtcTimestamp -Timestamp $commitTimeText
    $now = [DateTimeOffset]::UtcNow
    $elapsedMinutes = ($now - $commitTime).TotalMinutes

    $botComments = @($pr.comments.nodes) | Where-Object {
        (Test-IsCodexAuthor -Author $_.author) -and
        (
            (Test-BodyMentionsHead -Body ([string]$_.body) -CurrentHeadSha $currentHeadSha) -or
            (Test-TimestampOnOrAfter -Timestamp $_.createdAt -ReferenceTime $commitTime) -or
            (Test-TimestampOnOrAfter -Timestamp $_.updatedAt -ReferenceTime $commitTime)
        )
    }

    $botReviews = @($pr.reviews.nodes) | Where-Object {
        (Test-IsCodexAuthor -Author $_.author) -and
        ($null -ne $_.commit) -and
        ([string]$_.commit.oid -eq $currentHeadSha)
    }

    $botInlineComments = @($botReviews) | ForEach-Object { @($_.comments.nodes) } | Where-Object {
        (Test-IsCodexAuthor -Author $_.author) -or
        (Test-BodyMentionsHead -Body ([string]$_.body) -CurrentHeadSha $currentHeadSha)
    }

    if ($null -ne $eyesReaction) {
        Write-ReviewStatusBlock `
            -CurrentHeadSha $currentHeadSha `
            -BotState '👀 present for the current head; bot is reviewing' `
            -ManualTriggerDecision 'blocked; wait for 👍🏻 or findings' `
            -ManualTriggerLock 'active: 👀 is present for the current head' `
            -Details "PR: $($pr.url)"
        exit 10
    }

    if ($null -ne $thumbsUpReaction) {
        Write-ReviewStatusBlock `
            -CurrentHeadSha $currentHeadSha `
            -BotState '👍🏻 present for the current head; bot reported no issues' `
            -ManualTriggerDecision 'blocked; manual trigger is unnecessary' `
            -ManualTriggerLock 'active: no-issues bot state already exists' `
            -Details "PR: $($pr.url)"
        exit 11
    }

    if (@($botReviews).Count -gt 0) {
        Write-ReviewStatusBlock `
            -CurrentHeadSha $currentHeadSha `
            -BotState "bot review exists for current head ($(@($botReviews).Count))" `
            -ManualTriggerDecision 'blocked; process the existing bot review state' `
            -ManualTriggerLock 'active: bot review exists for the current head' `
            -Details "PR: $($pr.url)"
        exit 12
    }

    if (@($botComments).Count -gt 0 -or @($botInlineComments).Count -gt 0) {
        Write-ReviewStatusBlock `
            -CurrentHeadSha $currentHeadSha `
            -BotState "bot comment exists for current head (top-level: $(@($botComments).Count); inline: $(@($botInlineComments).Count))" `
            -ManualTriggerDecision 'blocked; route or resolve the existing bot findings' `
            -ManualTriggerLock 'active: bot comment exists for the current head' `
            -Details "PR: $($pr.url)"
        exit 13
    }

    if ($elapsedMinutes -lt $MinimumWaitMinutes) {
        $remaining = [Math]::Ceiling($MinimumWaitMinutes - $elapsedMinutes)
        Write-ReviewStatusBlock `
            -CurrentHeadSha $currentHeadSha `
            -BotState "no bot ack found yet; only $([Math]::Round($elapsedMinutes, 1)) minutes since head push" `
            -ManualTriggerDecision "blocked; wait at least $remaining more minute(s) before rechecking missed-ack exception" `
            -ManualTriggerLock 'active: minimum wait window has not elapsed' `
            -Details "PR: $($pr.url)"
        exit 14
    }

    Write-ReviewStatusBlock `
        -CurrentHeadSha $currentHeadSha `
        -BotState "no 👀, no 👍🏻, no bot review, and no bot comment found for current head after $([Math]::Round($elapsedMinutes, 1)) minutes" `
        -ManualTriggerDecision 'missed-ack exception allowed; a single documented manual trigger path may be used' `
        -ManualTriggerLock 'not active: guard verified the missed-ack exception conditions' `
        -Details "PR: $($pr.url)"
    exit 0
}
catch {
    Write-ReviewStatusBlock `
        -CurrentHeadSha 'unknown' `
        -BotState "ambiguous: $($_.Exception.Message)" `
        -ManualTriggerDecision 'blocked; do not manually trigger review from ambiguous state' `
        -ManualTriggerLock 'active: fail-closed guard could not prove missed-ack exception' `
        -Details 'Inspect GitHub state manually, then rerun this guard.'
    exit 1
}
