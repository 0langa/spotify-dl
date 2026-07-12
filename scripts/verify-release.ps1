param(
    [string]$ReleaseDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$checksumPath = Join-Path $directory 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $checksumPath)) {
    throw "Missing checksum file: $checksumPath"
}

$entries = Get-Content -LiteralPath $checksumPath | Where-Object { $_.Trim().Length -gt 0 }
if ($entries.Count -eq 0) {
    throw 'SHA256SUMS.txt contains no entries.'
}

foreach ($entry in $entries) {
    if ($entry -notmatch '^(?<hash>[0-9a-fA-F]{64})\s{2}(?<name>.+)$') {
        throw "Invalid checksum line: $entry"
    }
    $path = Join-Path $directory $Matches.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Checksummed file is missing: $($Matches.name)"
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $Matches.hash) {
        throw "Checksum mismatch: $($Matches.name)"
    }
}

$executable = Join-Path $directory 'PlaylistDL.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $executable
if ($signature.Status -eq 'HashMismatch' -or $signature.Status -eq 'NotTrusted') {
    throw "Unsafe Authenticode status: $($signature.Status)"
}
Write-Host "Release checksums verified. Authenticode status: $($signature.Status)."

$defender = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (Test-Path -LiteralPath $defender) {
    & $defender -Scan -ScanType 3 -File $executable -DisableRemediation
    if ($LASTEXITCODE -ne 0) {
        throw "Microsoft Defender scan failed or detected a threat (exit $LASTEXITCODE)."
    }
    Write-Host 'Microsoft Defender scan passed.'
}
else {
    Write-Warning 'Microsoft Defender CLI unavailable; checksum/signature validation still passed.'
}
