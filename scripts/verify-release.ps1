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

$verifiedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $entries) {
    if ($entry -notmatch '^(?<hash>[0-9a-fA-F]{64})\s{2}(?<name>.+)$') {
        throw "Invalid checksum line: $entry"
    }
    $name = $Matches.name
    if ([IO.Path]::IsPathRooted($name) -or [IO.Path]::GetFileName($name) -ne $name) {
        throw "Unsafe checksum path: $name"
    }
    $path = Join-Path $directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Checksummed file is missing: $($Matches.name)"
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $Matches.hash) {
        throw "Checksum mismatch: $($Matches.name)"
    }
    if (-not $verifiedNames.Add($name)) {
        throw "Duplicate checksum entry: $name"
    }
}

foreach ($file in Get-ChildItem -LiteralPath $directory -File | Where-Object Name -ne 'SHA256SUMS.txt') {
    if (-not $verifiedNames.Contains($file.Name)) {
        throw "Release file is not checksummed: $($file.Name)"
    }
}

$executable = Join-Path $directory 'PlaylistDL.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $executable
if ($signature.Status -notin @('Valid', 'NotSigned')) {
    throw "Unsafe Authenticode status: $($signature.Status)"
}
Write-Host "Release checksums verified. Authenticode status: $($signature.Status)."

$defender = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (Test-Path -LiteralPath $defender) {
    $scan = & $defender -Scan -ScanType 3 -File $executable -DisableRemediation 2>&1
    $scanExit = $LASTEXITCODE
    $scanOutput = ($scan | Out-String).Trim()
    if ($scanOutput.Length -gt 0) {
        Write-Host $scanOutput
    }

    # The antimalware service is turned off on hosted build agents, and MpCmdRun then
    # reports an HRESULT instead of a verdict. That is a missing scan, not a detection,
    # and it must not be reported as one.
    $engineUnavailable = $scanOutput -match 'CmdTool: Failed with hr' -or
        $scanOutput -match 'ScanFile failed with hr' -or
        $scanOutput -match '0x800106ba'
    if ($scanExit -ne 0 -and -not $engineUnavailable) {
        throw "Microsoft Defender detected a threat or failed to scan (exit $scanExit)."
    }

    if ($engineUnavailable) {
        Write-Warning (
            'Microsoft Defender did not run on this machine; ' +
            'checksum/signature validation still passed.')
    }
    else {
        Write-Host 'Microsoft Defender scan passed.'
    }
}
else {
    Write-Warning 'Microsoft Defender CLI unavailable; checksum/signature validation still passed.'
}
