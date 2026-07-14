param(
    [string]$BackendPath,
    [string]$ToolsBundle = 'artifacts/tools/playlistdl-tools.zip',
    [ValidateRange(1, 50)]
    [int]$Iterations = 10
)

$ErrorActionPreference = 'Stop'
$preparedBackend = & (Join-Path $PSScriptRoot 'prepare-smoke-backend.ps1') `
    -BackendPath $BackendPath `
    -ToolsBundle $ToolsBundle
$backend = $preparedBackend.Path

try {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $backend
        $startInfo.WorkingDirectory = Split-Path -Parent $backend
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($startInfo)
        try {
            $readyTask = $process.StandardOutput.ReadLineAsync()
            if (-not $readyTask.Wait(30000)) {
                throw "Iteration ${iteration}: backend did not become ready within 30 seconds."
            }
            $ready = $readyTask.Result | ConvertFrom-Json
            if ($ready.type -ne 'ready') {
                throw "Iteration ${iteration}: unexpected handshake '$($ready.type)'."
            }

            $process.StandardInput.WriteLine('{"id":"lifecycle","type":"shutdown"}')
            $process.StandardInput.Flush()
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(10000)) {
                throw "Iteration ${iteration}: backend did not exit after shutdown + stdin EOF."
            }
            if ($process.ExitCode -ne 0) {
                $stderr = $process.StandardError.ReadToEnd()
                throw "Iteration ${iteration}: backend exited $($process.ExitCode). $stderr"
            }
        }
        finally {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $process.WaitForExit(5000) | Out-Null
            }
            $process.Dispose()
        }
    }

    Write-Host "Backend lifecycle smoke passed $Iterations/$Iterations clean shutdowns."
}
finally {
    if ($preparedBackend.CleanupRoot -and (Test-Path -LiteralPath $preparedBackend.CleanupRoot)) {
        Remove-Item -LiteralPath $preparedBackend.CleanupRoot -Recurse -Force
    }
}
