[CmdletBinding()]
param(
    [string]$GamePath = "$env:APPDATA/Vintagestory",
    [string]$Package,
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root 'test-runs/client'
$data = Join-Path $run 'data'
$mods = Join-Path $data 'Mods'
$evidence = Join-Path $data 'EmberSmoke'
$client = $null
$watcher = $null
New-Item -ItemType Directory -Force $run, $data, $mods, $evidence | Out-Null
if (Test-Path (Join-Path $evidence 'done.json')) { throw 'Previous smoke evidence exists. Archive test-runs/client/data before starting another run.' }
try {
    $driver = Join-Path $mods 'Ember.Integration'
    & dotnet build (Join-Path $root 'tests/Ember.Integration/Ember.Integration.csproj') -c Release "-p:VintageStoryPath=$GamePath" -o $driver
    if ($LASTEXITCODE -ne 0) { throw 'Smoke driver build failed.' }
    if (-not $Package) {
        $artifact = Join-Path $root 'artifacts/emberdiagnostics-1.0.0.zip'
        if (Test-Path $artifact) { $Package = $artifact }
    }
    if ($Package) {
        Copy-Item -LiteralPath $Package -Destination (Join-Path $mods 'emberdiagnostics-1.0.0.zip') -Force
        Get-FileHash -LiteralPath $Package | Select-Object Algorithm, Hash | ConvertTo-Json | Set-Content (Join-Path $run 'package-hash.json')
    } else {
        $stage = Join-Path $mods 'Ember'
        $build = Join-Path $run 'mod-build'
        & dotnet build (Join-Path $root 'src/Ember.Mod/Ember.Mod.csproj') -c Release "-p:VintageStoryPath=$GamePath" -o $build
        if ($LASTEXITCODE -ne 0) { throw 'Ember build failed.' }
        New-Item -ItemType Directory -Force $stage | Out-Null
        foreach ($file in @('Ember.dll', 'Ember.Core.dll', 'modinfo.json')) { Copy-Item (Join-Path $build $file) $stage -Force }
        & dotnet publish (Join-Path $root 'src/Ember.Trace/Ember.Trace.csproj') -c Release --self-contained false -p:UseAppHost=false -o (Join-Path $stage 'tracer')
        if ($LASTEXITCODE -ne 0) { throw 'Tracer publish failed.' }
        '*tracer/*' | Set-Content (Join-Path $stage '.ignore')
        'Source-built mod and published tracer' | Set-Content (Join-Path $run 'package-source.txt')
    }
    # Read authorized settings privately; never emit their contents or use the real data directory for the game.
    $settings = Get-Content "$env:APPDATA/VintagestoryData/clientsettings.json" -Raw | ConvertFrom-Json -AsHashtable
    foreach ($pair in @{screenWidth=960;screenHeight=540;gameWindowMode=0;viewDistance=64;maxFps=60;shadowMapQuality=0;ssaoQuality=0;godRays=0;particleLevel=0;maxDynamicLights=0;cloudRenderMode=0;masterSoundLevel=0;vsyncMode=0}.GetEnumerator()) { $settings.intSettings[$pair.Key] = $pair.Value }
    foreach ($pair in @{pauseGameOnLostFocus=$false;showCreativeHelpDialog=$false;showSurvivalHelpDialog=$false;bloom=$false;fxaa=$false;wavingStuff=$false;ambientParticles=$false;multipleInstances=$true}.GetEnumerator()) { $settings.boolSettings[$pair.Key] = $pair.Value }
    $settings.floatSettings['ssaa'] = 1.0
    $settings.stringListSettings['modPaths'] = @((Join-Path $GamePath 'Mods'), $mods)
    $settings.stringListSettings['disabledMods'] = @()
    $settings.stringListSettings['multiplayerservers'] = @()
    $settings | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $data 'clientsettings.json')
    $settings = $null
    $watcher = [System.IO.FileSystemWatcher]::new($evidence, 'done.json')
    $watcher.EnableRaisingEvents = $true
    $args = @('--dataPath', ('"' + $data + '"'), '--openWorld', 'EmberSmoke', '--playStyle', 'creativebuilding')
    $client = Start-Process (Join-Path $GamePath 'Vintagestory.exe') -ArgumentList $args -WorkingDirectory $GamePath -PassThru -RedirectStandardOutput (Join-Path $run 'stdout.log') -RedirectStandardError (Join-Path $run 'stderr.log')
    @{ pid=$client.Id; startedUtc=[DateTimeOffset]::UtcNow; gameVersion='1.22.7'; dataPath=$data; arguments=$args } | ConvertTo-Json | Set-Content (Join-Path $run 'launch.json')
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path (Join-Path $evidence 'done.json'))) {
        if ($client.HasExited) { throw "Graphical client exited early ($($client.ExitCode))." }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Graphical client smoke timed out.' }
        $null = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::All, 1000)
    }
    & (Join-Path $root 'tests/Ember.Integration/capture-window.ps1') -Output 'graphical-client.png'
    $result = Get-Content (Join-Path $evidence 'done.json') -Raw | ConvertFrom-Json
    if (-not $result.success) { throw 'Driver reported failure; inspect private events.jsonl and game logs.' }
    Write-Host "Graphical smoke completed. Evidence: $run"
} finally {
    if ($watcher) { $watcher.Dispose() }
    if ($client -and -not $client.HasExited) {
        $null = $client.CloseMainWindow()
        if (-not $client.WaitForExit(15000)) { Stop-Process -Id $client.Id -Force; $client.WaitForExit() }
    }
    # The game may write backup copies on shutdown. Remove all copies inside the isolated run.
    Get-ChildItem -LiteralPath $data -Recurse -File -Filter '*clientsettings*' | Remove-Item -Force
}
