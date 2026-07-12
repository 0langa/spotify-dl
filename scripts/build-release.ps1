param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$backendDist = Join-Path $artifacts 'backend'
$pyinstallerWork = Join-Path $artifacts 'pyinstaller'
$staging = Join-Path $artifacts "tools-staging-$PID"
$tools = Join-Path $artifacts 'tools'
$release = Join-Path $artifacts 'release'
$repoPrefix = [IO.Path]::GetFullPath($repo) + [IO.Path]::DirectorySeparatorChar
$stagingFullPath = [IO.Path]::GetFullPath($staging)
if (-not $stagingFullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe staging path: $stagingFullPath"
}

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$ffprobe = (Get-Command ffprobe -ErrorAction Stop).Source
$deno = (Get-Command deno -ErrorAction Stop).Source

New-Item -ItemType Directory -Force -Path $backendDist, $pyinstallerWork, $staging, $tools, $release | Out-Null

Push-Location $repo
try {
    uv run --project backend pyinstaller `
        --clean `
        --noconfirm `
        --onefile `
        --name playlistdl-backend `
        --hidden-import _cffi_backend `
        --collect-all curl_cffi `
        --collect-all spotdl `
        --collect-all SpotipyFree `
        --collect-all spotapi `
        --collect-all pykakasi `
        --collect-all ytmusicapi `
        --distpath $backendDist `
        --workpath $pyinstallerWork `
        --specpath $pyinstallerWork `
        backend/src/playlistdl_backend/__main__.py

    Copy-Item -LiteralPath (Join-Path $backendDist 'playlistdl-backend.exe') -Destination $staging
    Copy-Item -LiteralPath $ffmpeg -Destination $staging
    Copy-Item -LiteralPath $ffprobe -Destination $staging
    Copy-Item -LiteralPath $deno -Destination $staging

    $files = Get-ChildItem -LiteralPath $staging -File | ForEach-Object {
        [ordered]@{
            name = $_.Name
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
    [ordered]@{
        version = $Version
        files = @($files)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding utf8

    $bundle = Join-Path $tools 'playlistdl-tools.zip'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $bundle -CompressionLevel Optimal -Force

    dotnet publish src/PlaylistDl.App/PlaylistDl.App.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $release `
        -p:Version=$Version

    $executable = Join-Path $release 'PlaylistDL.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw 'Release executable was not created.'
    }

    Get-ChildItem -LiteralPath $release -File |
        Where-Object Name -ne 'SHA256SUMS.txt' |
        ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
