# Copyright (c) 2026 dev-willbird1936
# Local validation and packaging only. This script never tags or publishes.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'DCU release preparation requires Windows and PowerShell 7.' }
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$project = Get-Content 'src/ShadowUse/ShadowUse.csproj'
    $version = [string]$project.Project.PropertyGroup.Version
    $stamp = Get-Date -AsUTC -Format 'yyyyMMdd-HHmmss-fff'
    $output = Join-Path $root "publish/candidate-$version-$stamp"
    # Never reuse or clear an existing output directory.
    if (Test-Path $output) { throw "Output already exists: $output" }
    New-Item -ItemType Directory -Path $output | Out-Null

    & dotnet test tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --logger "trx;LogFileName=release.trx"
    if ($LASTEXITCODE -ne 0) { throw 'Release .NET tests failed.' }
    & python -m unittest discover -s tests -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Python harness tests failed.' }

    $payload = Join-Path $output 'package'
    & dotnet publish src/ShadowUse/ShadowUse.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    $exe = Join-Path $payload 'dcu.exe'
    if (-not (Test-Path $exe)) { throw 'Publish did not produce dcu.exe.' }
    & python scripts/release_smoke.py $exe --version $version
    if ($LASTEXITCODE -ne 0) { throw 'Packaged MCP smoke test failed.' }

    Copy-Item LICENSE, NOTICE.md, README.md -Destination $payload
    $archive = Join-Path $output "dcu-$version-win-x64.zip"
    Compress-Archive -Path "$payload/*" -DestinationPath $archive
    # Retain the loose executable for clients upgrading from earlier releases.
    Copy-Item $exe -Destination $output
    $hashes = @('dcu.exe', (Split-Path $archive -Leaf)) | ForEach-Object {
        $hash = (Get-FileHash (Join-Path $output $_) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $_"
    }
    $hashes | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Validated local candidate: $output"
    Write-Host 'No tag, push, GitHub release, or deployment was performed.'
} finally {
    Pop-Location
}
