$codeFiles = Get-ChildItem -Include *.cs,*.csproj,*.fs,*.fsproj,*.yml,*.yaml,*.md -File -Recurse
$totalLines = 0
$files = [ordered]@{}
foreach ($codeFile in ($codeFiles | Where-Object {$_.FullName -notlike "*\obj\*" -and $_.FullName -notlike "*\bin\*" -and $_.FullName -notlike "*\.grace\*"})) {
    $stream = $codeFile.OpenText()
    $fileContents = $stream.ReadToEnd()
    $stream.Close()

    $lines = 0
    foreach ($line in $fileContents.Split("`n")) {
        if (-not [System.String]::IsNullOrEmpty($line) -and -not [System.String]::IsNullOrWhiteSpace($line)) {
            $lines += 1
        }
    }

    $files.Add($codeFile.FullName, $lines)
    $totalLines += $lines
}

$files | Format-Table -AutoSize

Write-Host -ForegroundColor Magenta "Total lines: $totalLines."
