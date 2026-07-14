param(
    [string]$BackendPath,
    [string]$ToolsBundle = 'artifacts/tools/playlistdl-tools.zip',
    [string]$SpotifyUrl = 'https://open.spotify.com/track/0yhPEz5KxlDwckGJaMlZqM'
)

$ErrorActionPreference = 'Stop'
$preparedBackend = & (Join-Path $PSScriptRoot 'prepare-smoke-backend.ps1') `
    -BackendPath $BackendPath `
    -ToolsBundle $ToolsBundle
$backend = $preparedBackend.Path
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $backend
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($startInfo)
try {
    $request = @{ id = 'release-smoke'; type = 'resolve'; url = $SpotifyUrl } | ConvertTo-Json -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()

    $readyTask = $process.StandardOutput.ReadLineAsync()
    if (-not $readyTask.Wait(30000)) {
        throw 'Frozen backend did not become ready within 30 seconds.'
    }
    $readyLine = $readyTask.Result
    if ([string]::IsNullOrWhiteSpace($readyLine)) {
        $process.WaitForExit(5000) | Out-Null
        $stderr = if ($process.HasExited) { $process.StandardError.ReadToEnd() } else { '' }
        throw "Frozen backend exited before its ready handshake. $stderr"
    }
    $ready = $readyLine | ConvertFrom-Json
    if ($ready.type -ne 'ready') {
        throw "Unexpected frozen backend handshake: $($ready.type)"
    }

    $responseTask = $process.StandardOutput.ReadLineAsync()
    if (-not $responseTask.Wait(60000)) {
        throw 'Frozen backend did not resolve Spotify metadata within 60 seconds.'
    }
    $response = $responseTask.Result | ConvertFrom-Json
    if ($response.type -ne 'playlist_resolved' -or $response.playlist.tracks.Count -ne 1) {
        throw "Frozen backend Spotify smoke failed: $($response.message ?? $response.type)"
    }
    Write-Host "Frozen backend resolved $($response.playlist.tracks[0].title)."

    $runtimeRequest = @{ id = 'runtime-smoke'; type = 'runtime_check' } | ConvertTo-Json -Compress
    $process.StandardInput.WriteLine($runtimeRequest)
    $process.StandardInput.Flush()
    $runtimeTask = $process.StandardOutput.ReadLineAsync()
    if (-not $runtimeTask.Wait(30000)) {
        throw 'Frozen backend provider resources did not load within 30 seconds.'
    }
    $runtime = $runtimeTask.Result | ConvertFrom-Json
    if ($runtime.type -ne 'runtime_ok') {
        throw "Frozen backend provider resource smoke failed: $($runtime.message ?? $runtime.type)"
    }
    Write-Host 'Frozen backend provider resources loaded.'

    $process.StandardInput.WriteLine('{"id":"release-shutdown","type":"shutdown"}')
    $process.StandardInput.Flush()
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) {
        throw 'Frozen backend did not exit cleanly after shutdown.'
    }
    if ($process.ExitCode -ne 0) {
        $stderr = $process.StandardError.ReadToEnd()
        throw "Frozen backend exited $($process.ExitCode) after shutdown. $stderr"
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000) | Out-Null
    }
    $process.Dispose()
    if ($preparedBackend.CleanupRoot -and (Test-Path -LiteralPath $preparedBackend.CleanupRoot)) {
        Remove-Item -LiteralPath $preparedBackend.CleanupRoot -Recurse -Force
    }
}
