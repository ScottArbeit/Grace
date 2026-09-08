#Requires -Version 7.6
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/Sqlite.ps1"
$script:RunRoot = Join-Path $PSScriptRoot ('run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
New-Item -ItemType Directory -Path $script:RunRoot | Out-Null
$script:Results = [Collections.Generic.List[object]]::new()
$control = @{ Fault=''; HookPoint=''; HookAction=$null; Active='' }

# Serializes captured model facts; the remote JSON is explicitly a server model, not client storage.
function Json($value) { ConvertTo-Json -InputObject $value -Depth 45 -Compress }
# Copies a value without retaining a client-side object across a simulated restart.
function Clone-Value($value) { Json $value | ConvertFrom-Json -AsHashtable }
# Escapes fixture data passed to the disposable native SQLite adapter.
function Q([string]$value) { "'" + $value.Replace("'", "''") + "'" }
# Opens a new real SQLite connection for each statement batch; failures roll back on close.
function Sql([string]$copy, [string]$statement) { ,([RenameSqlite]::Query((Join-Path $copy 'grace-local.db'), $statement)) }
# Rejects false experiment conclusions.
function Check([bool]$value, [string]$message) { if (-not $value) { throw "ASSERT:$message" } }
# Reads durable repository progress rather than a cached client projection.
function State([string]$copy) { (Sql $copy 'SELECT data FROM library_repository_state;')[0]['data'] | ConvertFrom-Json -AsHashtable }
# Reads materialized item rows from the local database.
function Items([string]$copy) { @((Sql $copy 'SELECT data FROM library_items ORDER BY id;') | ForEach-Object { $_['data'] | ConvertFrom-Json -AsHashtable }) }
# Reads pending and terminal operation records from the existing operation responsibility.
function Ops([string]$copy) { @((Sql $copy 'SELECT data FROM library_operations ORDER BY seq;') | ForEach-Object { $_['data'] | ConvertFrom-Json -AsHashtable }) }
# Selects one exact persisted operation identity.
function Op([string]$copy, [string]$id) { @(Ops $copy | Where-Object Id -EQ $id)[0] }
# Builds an insert for a local or incoming operation in the existing operation table.
function Insert-Op($op) { 'INSERT INTO library_operations(id,data) VALUES(' + (Q $op.Id) + ',' + (Q (Json $op)) + ');' }
# Builds an exact previous-record update; rows affected are checked by the caller.
function Update-Op($before, $after) { 'UPDATE library_operations SET data=' + (Q (Json $after)) + ' WHERE id=' + (Q $before.Id) + ' AND data=' + (Q (Json $before)) + ';' + (Require-One) }
# Aborts a transaction through SQLite integer overflow if the preceding protected update missed its exact row.
function Require-One { 'SELECT CASE WHEN changes()=1 THEN 1 ELSE abs(-9223372036854775808) END AS n;' }
# Atomically replaces one operation only if its previously observed record still matches.
function Save-Op([string]$copy, $before, $after) {
    $rows = Sql $copy ((Update-Op $before $after) + 'SELECT changes() AS n;')
    Check ($rows[0]['n'] -eq '1') 'exact operation compare-and-swap'
}
# Reads the persisted remote model after every server interaction.
function Remote([string]$case) { Get-Content -LiteralPath (Join-Path $case 'server-model.json') -Raw | ConvertFrom-Json -AsHashtable }
# Persists modeled server items, journal and receipts; this is not another local database/table.
function Save-Remote([string]$case, $remote) { [IO.File]::WriteAllText((Join-Path $case 'server-model.json'), (Json $remote)) }
# Computes a complete-byte identity for fixture assertions; production retains both BLAKE3 and SHA-256.
function Byte-Hash([byte[]]$bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) + ':' + $bytes.Length }
# Retains zero-byte files and directories as distinct physical obstructions.
function Fingerprint([string]$path) {
    if (Test-Path -LiteralPath $path) {
        $info = Get-Item -LiteralPath $path -Force
        if ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'BLOCK:reparse' }
        if ($info.PSIsContainer) { return 'directory' }
        return Byte-Hash ([IO.File]::ReadAllBytes($path))
    }
    return $null
}
# Maps a selected root-relative fixture name to the actual Windows filesystem.
function Path([string]$copy, [string]$name) { Join-Path (Join-Path $copy 'library') $name }
# Computes the expected identity of modeled retained content.
function Content-Hash($item) { if ($item.Deleted) { return $null }; Byte-Hash ([Text.Encoding]::UTF8.GetBytes($item.Bytes)) }
# Logs observations only; client recovery never reads this log.
function Log([string]$copy, [string]$effect) { [IO.File]::AppendAllText((Join-Path $copy 'effects.log'), $effect + "`n") }
# Captures all local database rows and actual files at an interruption boundary.
function Snapshot([string]$copy, [string]$point) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $copy 'library') -File -Recurse | ForEach-Object {
        @{ Name=$_.FullName; Hash=(Fingerprint $_.FullName); Ticks=$_.LastWriteTimeUtc.Ticks }
    })
    $value = @{ Point=$point; State=(State $copy); Items=@(Items $copy); Operations=@(Ops $copy); Files=$files }
    [IO.File]::WriteAllText((Join-Path $copy ('snapshot-' + $point + '.json')), (Json $value))
}
# Injects one supported external interleaving or loss of volatile client state.
function Hit([string]$copy, [string]$point) {
    if ($control.HookPoint -eq $point) { $control.HookPoint=''; & $control.HookAction $copy }
    if ($control.Fault -eq $point) { $control.Fault=''; Snapshot $copy $point; throw "CRASH:$point" }
}
# Uses a real exclusive Windows file handle to model the existing cooperative root lease.
function Lease([string]$copy) { [IO.FileStream]::new((Join-Path $copy '.grace/lease'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
# Rechecks catalog, exact progress and ordinary root immediately before effects.
function Guard([string]$copy, $expected) {
    Check ((Json (State $copy)) -eq (Json $expected)) 'persisted predecessor unchanged'
    if ((Remote (Split-Path $copy)).Catalog -ne $expected.Catalog) { throw 'BLOCK:catalog' }
    if ((Fingerprint (Join-Path $copy 'library')) -ne 'directory') { throw 'BLOCK:root' }
}
# Creates two real local SQLite databases, each with exactly the three existing table responsibilities.
function New-Case([string]$name) {
    $case = Join-Path $script:RunRoot $name
    New-Item -ItemType Directory -Path $case | Out-Null
    $initial = @(
        [ordered]@{ Id='file-1'; Name='source.txt'; Namespace=1; Revision=1; Version='content-X'; Bytes='original X'; Deleted=$false },
        [ordered]@{ Id='file-2'; Name='other.txt'; Namespace=1; Revision=1; Version='content-O'; Bytes='other O'; Deleted=$false }
    )
    Save-Remote $case ([ordered]@{ Catalog='catalog-1'; Cursor=10; Items=$initial; Changes=@(); Receipts=@{}; Submissions=0; Replays=0; Uploads=0 })
    foreach ($label in @('A','B')) {
        $copy = Join-Path $case $label
        New-Item -ItemType Directory -Path $copy, (Join-Path $copy 'library'), (Join-Path $copy '.grace') | Out-Null
        $state = [ordered]@{ Catalog='catalog-1'; Applied=10; Phase='current' }
        $schema = 'CREATE TABLE library_repository_state(id TEXT PRIMARY KEY,data TEXT NOT NULL); CREATE TABLE library_items(id TEXT PRIMARY KEY,data TEXT NOT NULL); CREATE TABLE library_operations(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,data TEXT NOT NULL);'
        $schema += 'INSERT INTO library_repository_state VALUES(' + (Q 'repository') + ',' + (Q (Json $state)) + ');'
        foreach ($item in $initial) {
            $schema += 'INSERT INTO library_items VALUES(' + (Q $item.Id) + ',' + (Q (Json $item)) + ');'
            [IO.File]::WriteAllText((Path $copy $item.Name), $item.Bytes)
        }
        Sql $copy $schema | Out-Null
    }
    $case
}
# Stores a clean same-parent rename intent without changing any filename or fabricating acceptance.
function Begin-Rename([string]$copy, [string]$source='source.txt', [string]$name='renamed.txt') {
    $lease = Lease $copy
    try {
        $pending = @(Ops $copy | Where-Object { $_.Kind -eq 'rename' -and -not $_.Terminal })
        if ($pending.Count -gt 0) {
            if ($pending[0].Source -ne $source -or $pending[0].Name -ne $name) { throw 'BLOCK:frozen-intent' }
            return $pending[0].Id
        }
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('/') -or $name.Contains('\') -or $source.ToUpperInvariant() -eq $name.ToUpperInvariant()) { throw 'BLOCK:name' }
        $state = State $copy; Guard $copy $state
        $remote = Remote (Split-Path $copy)
        if ($state.Applied -ne $remote.Cursor -or @(Ops $copy | Where-Object { -not $_.Terminal }).Count -gt 0) { throw 'BLOCK:not-current' }
        $matches = @(Items $copy | Where-Object { -not $_.Deleted -and $_.Name -eq $source })
        if ($matches.Count -ne 1) { throw 'BLOCK:source' }
        $item = $matches[0]
        if ((Fingerprint (Path $copy $source)) -ne (Content-Hash $item) -or (Get-Item -LiteralPath (Path $copy $source)).Length -eq 0) { throw 'BLOCK:dirty-source' }
        if ($null -ne (Fingerprint (Path $copy $name))) { throw 'BLOCK:destination' }
        $operation = [ordered]@{ Id=[guid]::NewGuid().ToString('D'); Kind='rename'; Source=$source; Name=$name; Base=(Clone-Value $item); Catalog=$state.Catalog; Cursor=$state.Applied; Request=$null; Receipt=$null; Accepted=$null; Prepared=$false; ApplyBase=$null; Target=$null; Expected=$null; Terminal=$false; Echo=$false; Rejection=$null }
        Hit $copy 'before-intent'
        if ($control.Fault -eq 'inside-intent') {
            $control.Fault=''; try { Sql $copy ('BEGIN IMMEDIATE;' + (Insert-Op $operation) + 'SELECT missing_injected_function();') | Out-Null } catch { Snapshot $copy 'inside-intent'; throw 'CRASH:inside-intent' }
        }
        Sql $copy (Insert-Op $operation) | Out-Null
        Hit $copy 'after-intent'
        $operation.Id
    } finally { $lease.Dispose() }
}
# Builds the actual rename request's relevant structural preconditions without a content precondition.
function Rename-Request($op) { [ordered]@{ Id=$op.Id; Kind='rename'; Item=$op.Base.Id; Namespace=$op.Base.Namespace; Name=$op.Name; Catalog=$op.Catalog; Predecessor=$op.Cursor } }
# Produces a stable request digest for the modeled permanent receipt protocol.
function Request-Key($request) { Json $request }
# Appends one explicitly modeled accepted server change in repository order.
function Accept($remote, [string]$id, [string]$kind, $item) {
    $previous = $remote.Cursor; $remote.Cursor++
    $change = [ordered]@{ Id=$id; Kind=$kind; Predecessor=$previous; Cursor=$remote.Cursor; Item=(Clone-Value $item) }
    $remote.Changes += $change
    $change
}
# Models the existing server namespace-only rename decision and immutable receipt replay.
function Submit-Rename([string]$case, $request) {
    $remote = Remote $case; $remote.Submissions++
    if ($remote.Receipts.Contains($request.Id)) {
        $receipt = $remote.Receipts[$request.Id]
        if ($receipt.Hash -ne (Request-Key $request)) { throw 'BLOCK:identity-mismatch' }
        $remote.Replays++; Save-Remote $case $remote; return $receipt
    }
    $item = @($remote.Items | Where-Object Id -EQ $request.Item)[0]
    $reason = $null
    if ($remote.Catalog -ne $request.Catalog) { $reason='StalePolicy' }
    elseif ($item.Deleted) { $reason='ItemTombstoned' }
    elseif ($item.Namespace -ne $request.Namespace) { $reason='NamespaceChanged' }
    elseif (@($remote.Items | Where-Object { -not $_.Deleted -and $_.Name -eq $request.Name -and $_.Id -ne $request.Item }).Count -gt 0) { $reason='SlotOccupied' }
    $change = $null
    if ($null -eq $reason) {
        $item.Name=$request.Name; $item.Namespace++
        $change = Accept $remote $request.Id 'rename' $item
    }
    $receipt = [ordered]@{ Id=$request.Id; Hash=(Request-Key $request); Outcome=$(if ($reason) {'rejected'} else {'accepted'}); Reason=$reason; Change=$change }
    $remote.Receipts[$request.Id]=$receipt; Save-Remote $case $remote
    $receipt
}
# Applies a serialized remote writer between client effects; it is an explicit server-model control.
function Remote-Change([string]$case, [string]$kind, [string]$id='file-1', [string]$value='remote Y') {
    $remote = Remote $case; $item = @($remote.Items | Where-Object Id -EQ $id)[0]
    switch ($kind) {
        'content' { $item.Bytes=$value; $item.Revision++; $item.Version='content-' + [guid]::NewGuid(); $remote.Uploads++ }
        'rename' { $item.Name=$value; $item.Namespace++ }
        'delete' { $item.Deleted=$true; $item.Namespace++ }
        default { throw 'Unknown modeled remote change.' }
    }
    Accept $remote ('remote-' + [guid]::NewGuid()) $kind $item | Out-Null
    Save-Remote $case $remote
}
# Freezes and submits pending rename requests and terminalizes only their definitive rejected receipts.
function Resolve-Intents([string]$copy) {
    foreach ($original in @(Ops $copy | Where-Object { $_.Kind -eq 'rename' -and -not $_.Terminal })) {
        $operation = $original
        if ($null -eq $operation.Request) {
            $next=Clone-Value $operation; $next.Request=Rename-Request $operation
            Hit $copy 'before-freeze'; Save-Op $copy $operation $next; Hit $copy 'after-freeze'; $operation=$next
        }
        if ($null -eq $operation.Receipt) {
            Hit $copy 'before-submit'
            $receipt = Submit-Rename (Split-Path $copy) $operation.Request
            Hit $copy 'after-server-decision'
            Check ($receipt.Id -eq $operation.Id -and $receipt.Hash -eq (Request-Key $operation.Request)) 'receipt matches frozen request'
            $next=Clone-Value $operation; $next.Receipt=$receipt; $next.Accepted=$receipt.Change
            Hit $copy 'before-receipt'; Save-Op $copy $operation $next; Hit $copy 'after-receipt'; $operation=$next
        }
        if ($operation.Receipt.Outcome -eq 'rejected') {
            Check ($null -eq $operation.Accepted -and -not $operation.Prepared) 'only rejected unprepared rename intent may retire'
            $before=State $copy; $next=Clone-Value $operation; $next.Terminal=$true; $next.Rejection=$operation.Receipt.Reason; $next.Echo=$false
            Hit $copy 'before-rejection'
            $sql='BEGIN IMMEDIATE;' + (Update-Op $operation $next)
            if ($control.Fault -eq 'inside-rejection') {
                $control.Fault=''; try { Sql $copy ($sql + 'SELECT missing_injected_function();') | Out-Null } catch { Snapshot $copy 'inside-rejection'; throw 'CRASH:inside-rejection' }
            }
            Sql $copy ($sql + 'COMMIT;') | Out-Null
            Check ((Json (State $copy)) -eq (Json $before)) 'rejection neither applies an item nor advances progress'
            Log $copy ('reject:' + $operation.Id); Hit $copy 'after-rejection'
        }
    }
}
# Captures one changed positive source/target using the existing saved-content operation responsibility.
function Save-Changed([string]$copy, [string]$name, $base) {
    $path = Path $copy $name; $hash=Fingerprint $path
    if ($null -eq $hash -or $hash -eq 'directory' -or (Get-Item -LiteralPath $path).Length -eq 0) { throw 'BLOCK:excluded-source' }
    $existing = @(Ops $copy | Where-Object { $_.Kind -eq 'save' -and $_.Source -eq $name -and $_.Hash -eq $hash -and -not $_.Terminal })
    if ($existing.Count) { return $existing[0] }
    $saved = [ordered]@{ Id=[guid]::NewGuid().ToString('D'); Kind='save'; Source=$name; Base=(Clone-Value $base); Bytes=[IO.File]::ReadAllText($path); Hash=$hash; Accepted=$null; Terminal=$false; Echo=$false; Rejection=$null }
    Sql $copy (Insert-Op $saved) | Out-Null; Log $copy ('capture-save:' + $saved.Id)
    $saved
}
# Models existing revision-based save acceptance; stale saves use an explicitly modeled conflict sibling.
function Accept-Save([string]$copy, $saved) {
    if ($null -ne $saved.Accepted) { return }
    $remote=Remote (Split-Path $copy)
    if ($null -eq $saved.Base) {
        $item=[ordered]@{ Id=('created-' + $saved.Id); Name=$saved.Source; Namespace=1; Revision=1; Version=('saved-' + $saved.Id); Bytes=$saved.Bytes; Deleted=$false }
        Check (@($remote.Items | Where-Object { -not $_.Deleted -and $_.Name -eq $item.Name }).Count -eq 0) 'positive create control destination free'
        $remote.Items += $item; $kind='create'
    } else {
    $item=@($remote.Items | Where-Object Id -EQ $saved.Base.Id)[0]
    if ($item.Deleted) { $next=Clone-Value $saved; $next.Rejection='ItemTombstoned'; Save-Op $copy $saved $next; throw 'BLOCK:saved-content-rejected' }
    if ($item.Revision -ne $saved.Base.Revision) {
        $item=[ordered]@{ Id=('conflict-' + $saved.Id); Name=('conflict-' + $saved.Id + '.txt'); Namespace=1; Revision=1; Version=('saved-' + $saved.Id); Bytes=$saved.Bytes; Deleted=$false }
        $remote.Items += $item; $kind='create'
    } else { $item.Bytes=$saved.Bytes; $item.Revision++; $item.Version='saved-' + $saved.Id; $kind='content' }
    }
    $remote.Uploads++; $change=Accept $remote $saved.Id $kind $item; Save-Remote (Split-Path $copy) $remote
    $next=Clone-Value $saved; $next.Accepted=$change; Save-Op $copy $saved $next
}
# Requires durable accepted saved bytes before overwriting a changed target or deleting a changed source.
function Require-Safe-Bytes([string]$copy, [string]$name, $base) {
    $actual=Fingerprint (Path $copy $name)
    if ($null -eq $actual -or $actual -eq (Content-Hash $base)) { return }
    if ($actual -eq 'directory' -or (Get-Item -LiteralPath (Path $copy $name)).Length -eq 0) { throw 'BLOCK:zero-or-directory' }
    $saved = Save-Changed $copy $name $base
    if ($null -eq $saved.Accepted -or (Content-Hash $saved.Accepted.Item) -ne $actual) { throw 'BLOCK:unaccepted-save' }
}
# Publishes one accepted change in feed order with real destination publication, source removal and atomic SQLite completion.
function Apply-Change([string]$copy, $change) {
    $state=State $copy; Guard $copy $state
    Check ($state.Applied -eq $change.Predecessor) 'accepted receipt cannot jump the ordered feed'
    $previous=@(Items $copy | Where-Object Id -EQ $change.Item.Id)
    $base = if ($previous.Count) {$previous[0]} else {$null}
    $source = if ($base) {$base.Name} else {$change.Item.Name}
    $target=$change.Item.Name; $targetPath=Path $copy $target; $sourcePath=Path $copy $source
    $matches=@(Ops $copy | Where-Object Id -EQ $change.Id)
    if (-not $matches.Count) {
        $operation=[ordered]@{ Id=$change.Id; Kind='remote'; Source=$source; Name=$target; Base=(Clone-Value $base); Accepted=(Clone-Value $change); Prepared=$false; ApplyBase=$null; Target=$null; Expected=$null; Terminal=$false; Echo=$false; Rejection=$null }
        Sql $copy (Insert-Op $operation) | Out-Null
    } else { $operation=$matches[0] }
    if ($operation.Kind -eq 'save') {
        # A modeled saved acceptance uses the same operation row, adding only incoming-effect preparation fields.
        $next=Clone-Value $operation
        foreach ($entry in @{ Prepared=$false; ApplyBase=$null; Target=$null; Expected=$null }.GetEnumerator()) { if (-not $next.Contains($entry.Key)) { $next[$entry.Key]=$entry.Value } }
        Save-Op $copy $operation $next; $operation=$next
    }
    if (-not $operation.Prepared) {
        $actual=Fingerprint $targetPath
        if ($source -ne $target -and $null -ne $actual) { throw 'BLOCK:destination' }
        if ($base -and $null -ne (Fingerprint $sourcePath)) { Require-Safe-Bytes $copy $source $base }
        $next=Clone-Value $operation; $next.Prepared=$true; $next.ApplyBase=(Clone-Value $base); $next.Target=$target; $next.Expected=$actual
        Hit $copy 'before-prepare'; Save-Op $copy $operation $next; Hit $copy 'after-prepare'; $operation=$next
    }
    Guard $copy $state
    Check ((Json (Op $copy $operation.Id)) -eq (Json $operation)) 'exact prepared operation before effect'
    $expected=Content-Hash $change.Item; $actual=Fingerprint $targetPath
    if ($null -ne $actual -and $actual -ne 'directory' -and (Get-Item -LiteralPath $targetPath).Length -eq 0) { throw 'BLOCK:zero-target' }
    if ($change.Item.Deleted) {
        if ($null -ne (Fingerprint $sourcePath)) {
            if ((Fingerprint $sourcePath) -ne (Content-Hash $base)) { throw 'BLOCK:delete-changed-source' }
            Hit $copy 'before-source-remove'; [IO.File]::Delete($sourcePath); Log $copy ('delete:' + $change.Id); Hit $copy 'after-source-remove'
        }
    } else {
        if ($base -and $source -ne $target -and $null -ne (Fingerprint $sourcePath)) { Require-Safe-Bytes $copy $source $base }
        if ($actual -ne $expected) {
            if ($actual -ne $operation.Expected) {
                if (-not $base) { throw 'BLOCK:target-obstruction' }
                Require-Safe-Bytes $copy $target $base
                $next=Clone-Value $operation; $next.Expected=$actual; Save-Op $copy $operation $next; $operation=$next
            }
            $stage=Join-Path $copy ('.grace/' + $change.Id + '.tmp')
            Hit $copy 'before-stage'
            $bytes=[Text.Encoding]::UTF8.GetBytes($change.Item.Bytes)
            $stream=[IO.FileStream]::new($stage,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None,65536,[IO.FileOptions]::WriteThrough)
            try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
            Check ((Fingerprint $stage) -eq $expected) 'retained accepted bytes verified after staging'
            Hit $copy 'after-stage'; Guard $copy $state
            if ((Fingerprint $targetPath) -ne $operation.Expected) { throw 'BLOCK:target-changed' }
            if ($base -and $source -ne $target -and $null -ne (Fingerprint $sourcePath)) { Require-Safe-Bytes $copy $source $base }
            Hit $copy 'before-publish'; [IO.File]::Move($stage,$targetPath,$true); Log $copy ('publish:' + $change.Id); Hit $copy 'after-publish'
        }
        if ($source -ne $target -and $null -ne (Fingerprint $sourcePath)) {
            Require-Safe-Bytes $copy $source $base
            Hit $copy 'before-source-remove'; Guard $copy $state; Require-Safe-Bytes $copy $source $base
            [IO.File]::Delete($sourcePath); Log $copy ('remove:' + $change.Id); Hit $copy 'after-source-remove'
        }
    }
    Guard $copy $state
    Check ((Fingerprint $targetPath) -eq $expected) 'filesystem complete before SQLite cursor'
    Check ($source -eq $target -or -not [IO.File]::Exists($sourcePath)) 'old source absent before progress'
    $next=Clone-Value $operation; $next.Terminal=$true; $next.Echo=-not $change.Item.Deleted
    $nextState=Clone-Value $state; $nextState.Applied=$change.Cursor; $nextState.Phase='catchingUp'
    $sql='BEGIN IMMEDIATE;INSERT INTO library_items(id,data) VALUES(' + (Q $change.Item.Id) + ',' + (Q (Json $change.Item)) + ') ON CONFLICT(id) DO UPDATE SET data=excluded.data;'
    foreach ($echo in @(Ops $copy | Where-Object { $_.Terminal -and $_.Echo -and $_.Accepted.Item.Id -eq $change.Item.Id })) {
        $retired=Clone-Value $echo; $retired.Echo=$false; $sql+=Update-Op $echo $retired
    }
    $sql+=Update-Op $operation $next
    $sql+='UPDATE library_repository_state SET data=' + (Q (Json $nextState)) + ' WHERE data=' + (Q (Json $state)) + ';' + (Require-One)
    Hit $copy 'before-complete'
    if ($control.Fault -eq 'inside-complete') {
        $control.Fault=''; try { Sql $copy ($sql + 'SELECT missing_injected_function();') | Out-Null } catch { Snapshot $copy 'inside-complete'; throw 'CRASH:inside-complete' }
    }
    Sql $copy ($sql + 'COMMIT;') | Out-Null
    Log $copy ('complete:' + $change.Id); Hit $copy 'after-complete'
}
# Resumes frozen requests, then consumes every actual modeled feed predecessor in order under one root lease.
function Run([string]$copy, [switch]$OnlyResolve) {
    $lease=Lease $copy
    try {
        Resolve-Intents $copy
        if ($OnlyResolve) { return }
        foreach ($change in @((Remote (Split-Path $copy)).Changes)) {
            if ($change.Cursor -gt (State $copy).Applied) { Apply-Change $copy $change }
        }
    } finally { $lease.Dispose() }
}
# Models Watch admission and exact terminal echoes, with an observable positive save/upload control.
function Watch([string]$copy) {
    try { $lease=Lease $copy } catch [IO.IOException] { Log $copy 'watch:busy'; return }
    try {
        $state=State $copy; Guard $copy $state
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $copy 'library') -File)) {
            $name=$file.Name; $actual=Fingerprint $file.FullName; Log $copy ('watch:observe:' + $name)
            if ($file.Length -eq 0) { continue }
            $materialized=@(Items $copy | Where-Object { -not $_.Deleted -and $_.Name -eq $name })
            $prepared=@(Ops $copy | Where-Object { -not $_.Terminal -and $_.Contains('Prepared') -and $_.Prepared -and $_.Target -eq $name -and $null -ne $_.Accepted -and $_.Accepted.Predecessor -eq $state.Applied -and ($_.Kind -ne 'rename' -or $_.Catalog -eq $state.Catalog) })
            if ($prepared.Count -eq 1 -and (Content-Hash $prepared[0].Accepted.Item) -eq $actual) {
                Check ((Json $prepared[0].ApplyBase) -eq (Json @(Items $copy | Where-Object Id -EQ $prepared[0].Accepted.Item.Id)[0])) 'prepared publication retains exact materialized ancestry'
                Log $copy ('watch:prepared-suppression:' + $name); continue
            }
            $item=if ($prepared.Count -eq 1) {$prepared[0].ApplyBase} elseif ($materialized.Count -eq 1) {$materialized[0]} else {$null}
            foreach ($echo in @(Ops $copy | Where-Object { $_.Terminal -and $_.Echo -and $_.Accepted.Item.Name -eq $name -and (Content-Hash $_.Accepted.Item) -eq $actual })) {
                $next=Clone-Value $echo; $next.Echo=$false; Save-Op $copy $echo $next; Log $copy ('echo:' + $echo.Id)
            }
            if ($null -eq $item -or $actual -ne (Content-Hash $item)) {
                $saved=Save-Changed $copy $name $item; Accept-Save $copy $saved
            }
        }
    } finally { $lease.Dispose() }
}
# Exercises the existing bounded classified-history filter in this no-baseline, no-create-dependency fixture.
function Prune-Classified([string]$copy, [int]$retainedCount) {
    $terminal=@(Ops $copy | Where-Object Terminal); [array]::Reverse($terminal)
    foreach ($candidate in @($terminal | Select-Object -Skip $retainedCount)) {
        if (-not $candidate.Echo -and ($null -eq $candidate.Accepted -or $candidate.Accepted.Cursor -ne (State $copy).Applied)) {
            Sql $copy ('DELETE FROM library_operations WHERE id=' + (Q $candidate.Id) + ' AND data=' + (Q (Json $candidate)) + ';') | Out-Null
        }
    }
}
# Verifies exact bytes and progress after convergence, then proves restart performs no more publication or upload.
function Verify-Converged([string]$case) {
    $remote=Remote $case
    foreach ($label in @('A','B')) {
        $copy=Join-Path $case $label; Run $copy; Watch $copy; $remote=Remote $case
        Check ((State $copy).Applied -eq $remote.Cursor) 'all genuine predecessors applied'
        foreach ($item in $remote.Items) {
            if (-not $item.Deleted) { Check ((Fingerprint (Path $copy $item.Name)) -eq (Content-Hash $item)) 'exact final bytes' }
        }
        Check (((@(Get-ChildItem -LiteralPath (Join-Path $copy 'library') -File | ForEach-Object Name | Sort-Object)) -join '|') -eq ((@($remote.Items | Where-Object { -not $_.Deleted } | ForEach-Object Name | Sort-Object)) -join '|')) 'exact namespace with no duplicate old names'
        $ticks=@{}; foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $copy 'library') -File)) { $ticks[$file.Name]=$file.LastWriteTimeUtc.Ticks }
        $uploads=(Remote $case).Uploads; Run $copy; Watch $copy
        Check ((Remote $case).Uploads -eq $uploads) 'restart has no duplicate upload'
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $copy 'library') -File)) { Check ($ticks[$file.Name] -eq $file.LastWriteTimeUtc.Ticks) 'restart does not rewrite completed files' }
        Check (@(Ops $copy | Where-Object { -not $_.Terminal }).Count -eq 0) 'no pending completed rename or accepted save'
        Check ((Sql $copy "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'library_%';").Count -eq 3) 'exactly three local Library tables'
    }
}
# Runs an isolated scenario and retains evidence even when its assertion fails.
function Case([string]$name, [scriptblock]$action) {
    $case=New-Case $name; $control.Active=$case; $control.Fault=''; $control.HookPoint=''; $control.HookAction=$null
    $passed=$false; $errorText=$null
    try { & $action $case (Join-Path $case 'A') (Join-Path $case 'B'); $passed=$true }
    catch { $errorText=$_.ToString() + ' at ' + $_.ScriptStackTrace }
    $record=[ordered]@{ Name=$name; Passed=$passed; Error=$errorText; Directory=$case; Remote=(Remote $case); A=@{State=(State (Join-Path $case 'A')); Operations=@(Ops (Join-Path $case 'A'))}; B=@{State=(State (Join-Path $case 'B')); Operations=@(Ops (Join-Path $case 'B'))} }
    $script:Results.Add($record); Write-Output ($name + ': ' + $passed + $(if ($errorText) {' ' + $errorText} else {''}))
}
# Requires an intended block or injected interruption instead of accepting an arbitrary exception.
function Expect([string]$pattern, [scriptblock]$action) { try { & $action; throw 'ASSERT:expected failure missing' } catch { if ($_.Exception.Message -notlike $pattern) { throw } } }

Case 'happy-two-copy-restart' { param($case,$a,$b)
    Begin-Rename $a | Out-Null; Run $a; Verify-Converged $case
    $item=@((Remote $case).Items | Where-Object Id -EQ 'file-1')[0]
    Check ($item.Name -eq 'renamed.txt' -and $item.Revision -eq 1 -and $item.Version -eq 'content-X') 'rename keeps identity and content revision'
    Check (-not [IO.File]::Exists((Path $a 'source.txt')) -and (Remote $case).Uploads -eq 0) 'rename is not a duplicate create/upload'
}
$boundaries=@('before-intent','inside-intent','after-intent','before-freeze','after-freeze','before-submit','after-server-decision','before-receipt','after-receipt','before-prepare','after-prepare','before-stage','after-stage','before-publish','after-publish','before-source-remove','after-source-remove','before-complete','inside-complete','after-complete')
foreach ($boundary in $boundaries) {
    $point=$boundary
    Case ('restart-' + $point) {
        param($case,$a,$b)
        $control.Fault=$point
        Expect 'CRASH:*' { Begin-Rename $a | Out-Null; Run $a }
        Check ((State $a).Applied -eq $(if ($point -eq 'after-complete') {11} else {10})) 'progress remains at predecessor until committed completion'
        $published=if ([IO.File]::Exists((Path $a 'renamed.txt'))) {(Get-Item -LiteralPath (Path $a 'renamed.txt')).LastWriteTimeUtc.Ticks} else {$null}
        Watch $a
        if (@(Ops $a | Where-Object Kind -EQ 'rename').Count -eq 0) { Begin-Rename $a | Out-Null }
        Run $a; Verify-Converged $case
        Check (@((Remote $case).Changes | Where-Object Kind -EQ 'rename').Count -eq 1) 'one accepted rename across restart'
        Check ((Remote $case).Uploads -eq 0) 'partial rename never becomes a Watch upload'
        if ($null -ne $published) { Check ((Get-Item -LiteralPath (Path $a 'renamed.txt')).LastWriteTimeUtc.Ticks -eq $published) 'recovery preserves already published destination timestamp' }
        Check (@(Get-Content -LiteralPath (Join-Path $a 'effects.log') | Where-Object { $_ -like 'publish:*' }).Count -eq 1) 'one physical publication across interruption and recovery'
    }.GetNewClosure()
}
Case 'duplicate-and-different-invocation' { param($case,$a,$b)
    $id=Begin-Rename $a; Check ((Begin-Rename $a) -eq $id) 'duplicate pending invocation finds frozen identity'
    Expect 'BLOCK:frozen-intent' { Begin-Rename $a 'source.txt' 'different.txt' }
    Run $a; Verify-Converged $case
    Check ((Remote $case).Changes.Count -eq 1) 'different request cannot replace pending name'
}
Case 'receipt-replay-before-current-preconditions' { param($case,$a,$b)
    $id=Begin-Rename $a; $control.Fault='after-server-decision'; Expect 'CRASH:*' { Run $a }
    Remote-Change $case 'rename' 'file-1' 'remote-final.txt'
    Run $a; Verify-Converged $case
    Check ((Remote $case).Replays -eq 1 -and (Op $a $id).Accepted.Item.Name -eq 'renamed.txt') 'receipt replay retains original acceptance despite later rename'
}
foreach ($boundary in @('before-rejection','inside-rejection','after-rejection')) {
    $point=$boundary
    Case ('rejection-restart-' + $point) {
        param($case,$a,$b)
        $id=Begin-Rename $a; Remote-Change $case 'rename' 'file-1' 'remote-name.txt'; $control.Fault=$point
        Expect 'CRASH:*' { Run $a -OnlyResolve }
        Check ([IO.File]::Exists((Path $a 'source.txt')) -and -not [IO.File]::Exists((Path $a 'renamed.txt')) -and (State $a).Applied -eq 10) 'rejection makes no rename effect or cursor progress'
        Run $a -OnlyResolve; Check ((Op $a $id).Terminal -and (Op $a $id).Rejection -eq 'NamespaceChanged' -and -not (Op $a $id).Echo) 'rejected intent retired without echo'
        Remote-Change $case 'content' 'file-2' 'unrelated edit'; Verify-Converged $case
    }.GetNewClosure()
}
Case 'two-items-compete-for-name' { param($case,$a,$b)
    $id=Begin-Rename $a; Remote-Change $case 'rename' 'file-2' 'renamed.txt'; Run $a -OnlyResolve
    Check ((Op $a $id).Rejection -eq 'SlotOccupied' -and [IO.File]::Exists((Path $a 'source.txt'))) 'losing source remains untouched'
    Verify-Converged $case
}
Case 'remote-delete-before-acceptance' { param($case,$a,$b)
    $id=Begin-Rename $a; Remote-Change $case 'delete'; Run $a -OnlyResolve
    Check ((Op $a $id).Rejection -eq 'ItemTombstoned' -and [IO.File]::Exists((Path $a 'source.txt'))) 'rejection itself does not remove source'
    Verify-Converged $case
}
Case 'compatible-content-before-rename' { param($case,$a,$b)
    $id=Begin-Rename $a; Remote-Change $case 'content'; Run $a -OnlyResolve
    Check ((Op $a $id).Accepted.Item.Bytes -eq 'remote Y' -and (State $a).Applied -eq 10) 'accepted receipt newer content is not immediate application'
    Verify-Converged $case
}
Case 'compatible-content-after-rename' { param($case,$a,$b)
    Begin-Rename $a | Out-Null; Run $a -OnlyResolve; Remote-Change $case 'content'; Verify-Converged $case
}
Case 'occupied-destination-at-admission' { param($case,$a,$b)
    [IO.File]::WriteAllText((Path $a 'renamed.txt'),'local obstruction'); Expect 'BLOCK:destination' { Begin-Rename $a }
    Check (@(Ops $a).Count -eq 0 -and (Remote $case).Changes.Count -eq 0) 'obstruction before intent has no durable request'
}
foreach ($where in @('source','target')) {
    $which=$where
    Case ('zero-obstruction-' + $which) {
        param($case,$a,$b)
        Begin-Rename $a | Out-Null; Run $a -OnlyResolve
        $name=if ($which -eq 'source') {'source.txt'} else {'renamed.txt'}
        [IO.File]::WriteAllBytes((Path $a $name),[byte[]]::new(0)); Expect 'BLOCK:*' { Run $a }
        Check ((Get-Item -LiteralPath (Path $a $name)).Length -eq 0 -and (State $a).Applied -eq 10) 'zero-byte obstruction preserved without false progress'
    }.GetNewClosure()
}
Case 'catalog-change-after-acceptance' { param($case,$a,$b)
    Begin-Rename $a | Out-Null; Run $a -OnlyResolve
    $remote=Remote $case; $remote.Catalog='catalog-2'; Save-Remote $case $remote
    Expect 'BLOCK:catalog' { Run $a }; Check ((State $a).Applied -eq 10 -and [IO.File]::Exists((Path $a 'source.txt'))) 'catalog change blocks application'
}
Case 'catalog-change-before-publication' { param($case,$a,$b)
    Begin-Rename $a | Out-Null
    $control.HookPoint='after-stage'; $control.HookAction={ param($copy) $case=Split-Path $copy; $remote=Remote $case; $remote.Catalog='catalog-2'; Save-Remote $case $remote }
    Expect 'BLOCK:catalog' { Run $a }; Check ((State $a).Applied -eq 10 -and -not [IO.File]::Exists((Path $a 'renamed.txt'))) 'catalog rechecked after staging'
}
Case 'target-obstruction-after-staging' { param($case,$a,$b)
    Begin-Rename $a | Out-Null
    $control.HookPoint='after-stage'; $control.HookAction={ param($copy) [IO.File]::WriteAllText((Path $copy 'renamed.txt'),'new obstruction') }
    Expect 'BLOCK:target-changed' { Run $a }; Check ([IO.File]::ReadAllText((Path $a 'renamed.txt')) -eq 'new obstruction' -and (State $a).Applied -eq 10) 'late obstruction preserved'
}
foreach ($where in @('source','target')) {
    $which=$where
    Case ('saved-edit-after-publication-' + $which) {
        param($case,$a,$b)
        Begin-Rename $a | Out-Null; $control.Fault='after-publish'; Expect 'CRASH:*' { Run $a }
        $name=if ($which -eq 'source') {'source.txt'} else {'renamed.txt'}
        [IO.File]::WriteAllText((Path $a $name),'saved Z')
        Expect 'BLOCK:unaccepted-save' { Run $a }
        $saved=@(Ops $a | Where-Object Kind -EQ 'save')[0]; Check ($saved.Base.Revision -eq 1 -and $saved.Bytes -eq 'saved Z') 'saved edit retains original materialized base'
        Accept-Save $a $saved; Run $a; Verify-Converged $case
        Check ([IO.File]::ReadAllText((Path $a 'renamed.txt')) -eq 'saved Z') 'accepted compatible saved edit survives rename'
    }.GetNewClosure()
}
Case 'stale-saved-edit-conflict-preserved' { param($case,$a,$b)
    Begin-Rename $a | Out-Null; Remote-Change $case 'content'; Run $a -OnlyResolve
    [IO.File]::WriteAllText((Path $a 'source.txt'),'losing Z'); Expect 'BLOCK:unaccepted-save' { Run $a }
    $saved=@(Ops $a | Where-Object Kind -EQ 'save')[0]; Accept-Save $a $saved; Verify-Converged $case
    $conflict=@((Remote $case).Items | Where-Object { $_.Id.StartsWith('conflict-') })[0]
    Check ([IO.File]::ReadAllText((Path $a $conflict.Name)) -eq 'losing Z' -and [IO.File]::ReadAllText((Path $a 'renamed.txt')) -eq 'remote Y') 'existing revision conflict retains both values'
}
Case 'content-rejection-remains-pending' { param($case,$a,$b)
    [IO.File]::WriteAllText((Path $a 'source.txt'),'saved Z'); $saved=Save-Changed $a 'source.txt' @(Items $a | Where-Object Id -EQ 'file-1')[0]
    Remote-Change $case 'delete'; Expect 'BLOCK:saved-content-rejected' { Accept-Save $a $saved }; Run $a -OnlyResolve
    Check (-not (Op $a $saved.Id).Terminal -and (Op $a $saved.Id).Bytes -eq 'saved Z') 'rename rejection retirement does not retire saved-content rejection'
}
Case 'watch-lease-gate-and-positive-control' { param($case,$a,$b)
    Begin-Rename $a | Out-Null
    $control.HookPoint='after-publish'; $control.HookAction={ param($copy) Watch $copy }
    Run $a; Verify-Converged $case
    Check ((Get-Content -LiteralPath (Join-Path $a 'effects.log')) -contains 'watch:busy') 'concurrent Watch actually observes lease contention'
    Check ((Remote $case).Uploads -eq 0) 'rename effects do not upload'
    [IO.File]::WriteAllText((Path $a 'renamed.txt'),'positive Watch save'); Watch $a
    Check ((Remote $case).Uploads -eq 1) 'positive capture control uploads after completion'; Verify-Converged $case
}
Case 'cancellation-leaves-frozen-intent-resumable' { param($case,$a,$b)
    Begin-Rename $a | Out-Null
    $control.HookPoint='before-submit'; $control.HookAction={ param($copy) throw [OperationCanceledException]::new('CANCEL:before-submit') }
    Expect 'CANCEL:*' { Run $a }; Check ((Remote $case).Changes.Count -eq 0) 'cancel before submission has no remote change'; Verify-Converged $case
}
Case 'watch-observes-partial-destination-and-positive-create' { param($case,$a,$b)
    Begin-Rename $a | Out-Null; $control.Fault='after-publish'; Expect 'CRASH:*' { Run $a }
    Watch $a
    $effects=Get-Content -LiteralPath (Join-Path $a 'effects.log')
    Check ($effects -contains 'watch:observe:renamed.txt' -and $effects -contains 'watch:prepared-suppression:renamed.txt' -and (Remote $case).Uploads -eq 0) 'prepared destination observed and exactly suppressed after released lease'
    [IO.File]::WriteAllText((Path $a 'positive-new.txt'),'positive untracked create'); Watch $a
    Check ((Remote $case).Uploads -eq 1 -and @((Remote $case).Changes | Where-Object Kind -EQ 'create').Count -eq 1) 'untracked positive file is recognized even while rename pending'
    Verify-Converged $case
}
foreach ($drift in @('operation','predecessor')) {
    $which=$drift
    Case ('completion-cas-rollback-' + $which) {
        param($case,$a,$b)
        $id=Begin-Rename $a
        $control.HookPoint='before-complete'; $control.HookAction={ param($copy)
            if ($which -eq 'operation') {
                $old=Op $copy $id; $changed=Clone-Value $old; $changed['ObservedDrift']='guard-test'; Save-Op $copy $old $changed
            } else {
                $state=State $copy; $state.Phase='drift-test'; Sql $copy ('UPDATE library_repository_state SET data=' + (Q (Json $state)) + ';') | Out-Null
            }
        }.GetNewClosure()
        Expect '*integer overflow*' { Run $a }
        Check ((State $a).Applied -eq 10 -and -not (Op $a $id).Terminal -and @(Items $a | Where-Object Id -EQ 'file-1')[0].Name -eq 'source.txt') 'missed exact row rolls back items terminal and cursor together'
        Verify-Converged $case
    }.GetNewClosure()
}
Case 'rejection-cas-rollback' { param($case,$a,$b)
    $id=Begin-Rename $a; Remote-Change $case 'rename' 'file-1' 'remote-name.txt'
    $control.HookPoint='before-rejection'; $control.HookAction={ param($copy) $old=Op $copy $id; $next=Clone-Value $old; $next['ObservedDrift']='guard-test'; Save-Op $copy $old $next }.GetNewClosure()
    Expect '*integer overflow*' { Run $a -OnlyResolve }
    Check (-not (Op $a $id).Terminal -and (State $a).Applied -eq 10 -and [IO.File]::Exists((Path $a 'source.txt'))) 'failed rejection CAS leaves intent pending and filesystem unchanged'
    Verify-Converged $case
}
Case 'lost-rejection-response-and-bounded-pruning' { param($case,$a,$b)
    $id=Begin-Rename $a; Remote-Change $case 'rename' 'file-1' 'remote-name.txt'
    $control.Fault='after-server-decision'; Expect 'CRASH:*' { Run $a -OnlyResolve }
    Remote-Change $case 'content' 'file-2' 'unrelated edit'; Run $a -OnlyResolve
    Check ((Op $a $id).Terminal -and (Op $a $id).Rejection -eq 'NamespaceChanged' -and (Remote $case).Replays -eq 1) 'lost rejected receipt replays original definitive outcome'
    Check ((State $a).Applied -eq 10 -and [IO.File]::Exists((Path $a 'source.txt'))) 'rejection itself has no accepted progress or filename effect'
    Verify-Converged $case; Prune-Classified $a 1
    Check (@(Ops $a | Where-Object Id -EQ $id).Count -eq 0) 'old classified rejected rename uses existing retained-history filter'
    Check (@(Ops $a | Where-Object { $null -ne $_.Accepted -and $_.Accepted.Cursor -eq (State $a).Applied }).Count -eq 1) 'pruning retains applied tip'
}
Case 'pruning-retains-rejected-content-and-unobserved-echo' { param($case,$a,$b)
    Begin-Rename $a | Out-Null; Run $a
    [IO.File]::WriteAllText((Path $a 'renamed.txt'),'retained rejected bytes'); $saved=Save-Changed $a 'renamed.txt' @(Items $a | Where-Object Id -EQ 'file-1')[0]
    Remote-Change $case 'delete'; Expect 'BLOCK:saved-content-rejected' { Accept-Save $a $saved }
    Prune-Classified $a 0
    Check (@(Ops $a | Where-Object { $_.Terminal -and $_.Echo }).Count -eq 1 -and -not (Op $a $saved.Id).Terminal) 'existing echo and pending saved-content protections remain'
}
Case 'same-operation-different-request-rejected' { param($case,$a,$b)
    $id=Begin-Rename $a; Run $a -OnlyResolve; $request=Clone-Value (Op $a $id).Request; $request.Name='illegal-replay.txt'
    Expect 'BLOCK:identity-mismatch' { Submit-Rename $case $request | Out-Null }; Verify-Converged $case
}

$failed=@($script:Results | Where-Object { -not $_.Passed })
$output=[ordered]@{ Question='Can one explicit same-parent nonempty-file rename use the existing three local table responsibilities across acceptance, definitive rejection, ordered application and restart?'; Base='8ce7cd4dda7089e801662dfc410bab4dcbf0ff81'; Platform=@{ OS=[Environment]::OSVersion.VersionString; PowerShell=$PSVersionTable.PSVersion.ToString(); SQLite=[RenameSqlite]::Version(); Filesystem=(Get-Volume -DriveLetter C).FileSystem }; Passed=$script:Results.Count-$failed.Count; Failed=$failed.Count; RunRoot=$script:RunRoot; Cases=$script:Results }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'results.json'), (ConvertTo-Json -InputObject $output -Depth 50))
[IO.File]::WriteAllText((Join-Path $script:RunRoot 'results.json'), (ConvertTo-Json -InputObject $output -Depth 50))
Write-Output ('RESULT: ' + $output.Passed + '/' + $script:Results.Count)
if ($failed.Count) { exit 1 }
