param(
    [string]$BackendPath,
    [string]$ToolsBundle = 'artifacts/tools/playlistdl-tools.zip'
)

$ErrorActionPreference = 'Stop'

if ($BackendPath) {
    return [pscustomobject]@{
        Path = (Resolve-Path -LiteralPath $BackendPath).Path
        CleanupRoot = $null
    }
}

$bundle = (Resolve-Path -LiteralPath $ToolsBundle).Path
$cleanupRoot = Join-Path ([IO.Path]::GetTempPath()) "playlistdl-smoke-$PID-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $cleanupRoot | Out-Null

try {
    Expand-Archive -LiteralPath $bundle -DestinationPath $cleanupRoot
    $manifest = Get-Content -LiteralPath (Join-Path $cleanupRoot 'manifest.json') -Raw | ConvertFrom-Json
    $backendEntry = $manifest.files | Where-Object name -eq 'playlistdl-backend.exe' | Select-Object -First 1
    if (-not $backendEntry) {
        throw 'Bundled tools manifest does not contain playlistdl-backend.exe.'
    }

    $backend = Join-Path $cleanupRoot $backendEntry.name
    $actualHash = (Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash
    if ($actualHash -ne $backendEntry.sha256) {
        throw 'Bundled backend hash does not match its manifest.'
    }

    return [pscustomobject]@{
        Path = $backend
        CleanupRoot = $cleanupRoot
    }
}
catch {
    if (Test-Path -LiteralPath $cleanupRoot) {
        Remove-Item -LiteralPath $cleanupRoot -Recurse -Force
    }
    throw
}
