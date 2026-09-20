[CmdletBinding()]
param([string]$GamePath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $GamePath) { $GamePath = $env:VINTAGE_STORY }
if (-not $GamePath -and $env:APPDATA) { $GamePath = Join-Path $env:APPDATA 'Vintagestory' }
if (-not $GamePath) { throw 'Pass -GamePath or set VINTAGE_STORY to a Vintage Story 1.22.7 installation.' }
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
foreach ($file in @('VintagestoryAPI.dll', 'VintagestoryLib.dll', 'Lib/0Harmony.dll', 'Lib/Newtonsoft.Json.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $GamePath $file) -PathType Leaf)) { throw "Missing game reference: $file in $GamePath" }
}
Get-Command dotnet -ErrorAction Stop | Out-Null
$metadata = Get-Content -LiteralPath (Join-Path $root 'src/Ember.Mod/modinfo.json') -Raw | ConvertFrom-Json
$version = [string]$metadata.version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw "Invalid modinfo version: $version" }
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$work = Join-Path $artifacts ('.package-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'package'
$zip = Join-Path $artifacts "emberdiagnostics-$version.zip"
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    & dotnet build (Join-Path $root 'src/Ember.Mod/Ember.Mod.csproj') --configuration Release --framework net10.0 "-p:VintageStoryPath=$GamePath" "-p:Version=$version"
    if ($LASTEXITCODE -ne 0) { throw "Mod build failed ($LASTEXITCODE)." }
    & dotnet publish (Join-Path $root 'src/Ember.Trace/Ember.Trace.csproj') --configuration Release --framework net10.0 --self-contained false -p:UseAppHost=false "-p:Version=$version" --output (Join-Path $stage 'tracer')
    if ($LASTEXITCODE -ne 0) { throw "Tracer publish failed ($LASTEXITCODE)." }
    $modOutput = Join-Path $root 'src/Ember.Mod/bin/Release/net10.0'
    foreach ($file in @('Ember.dll', 'Ember.Core.dll')) { Copy-Item -LiteralPath (Join-Path $modOutput $file) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $root 'src/Ember.Mod/modinfo.json') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'src/Ember.Mod/.ignore') -Destination $stage
    foreach ($file in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'AI_USAGE.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'SECURITY.md', 'CODE_OF_CONDUCT.md', 'DESIGN.md', 'PRODUCT.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $stage 'docs') -Recurse
    foreach ($file in @('Ember.Trace.dll', 'Ember.Trace.deps.json', 'Ember.Trace.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage "tracer/$file") -PathType Leaf)) { throw "Missing published tracer file: $file" }
    }
    $forbidden = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -like 'Vintagestory*.dll' -or $_.Name -in @('0Harmony.dll', 'Newtonsoft.Json.dll') }
    if ($forbidden) { throw "Game DLLs found in package: $($forbidden.Name -join ', ')" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, (Join-Path $work 'package.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath (Join-Path $work 'package.zip') -Destination $zip -Force
    Write-Host "Package: $zip"
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
    $hash
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
