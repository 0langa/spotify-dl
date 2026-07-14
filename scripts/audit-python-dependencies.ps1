$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$requirements = New-TemporaryFile
try {
    Push-Location $repo
    try {
        uv export `
            --project backend `
            --locked `
            --no-dev `
            --no-emit-project `
            --format requirements-txt `
            --quiet `
            --output-file $requirements.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "uv export failed with exit code $LASTEXITCODE."
        }

        # spotDL 4.5.0 pins an obsolete FastAPI/Starlette server stack. Playlist DL never
        # starts that server, and release builds explicitly exclude fastapi, starlette,
        # and uvicorn. smoke-frozen-backend.ps1 verifies those modules remain absent.
        $excludedServerAdvisories = @(
            'PYSEC-2024-38',
            'PYSEC-2026-161',
            'PYSEC-2026-249',
            'PYSEC-2026-248',
            'PYSEC-2026-1943',
            'PYSEC-2026-1941',
            'PYSEC-2026-2281',
            'PYSEC-2026-2280'
        )
        $ignoreArguments = $excludedServerAdvisories | ForEach-Object { '--ignore-vuln'; $_ }
        uvx pip-audit==2.10.1 -r $requirements.FullName @ignoreArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Python dependency audit failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    Remove-Item -LiteralPath $requirements.FullName -Force -ErrorAction SilentlyContinue
}
