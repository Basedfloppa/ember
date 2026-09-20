[CmdletBinding()]
param([string]$RunPath = (Join-Path $PSScriptRoot '../../test-runs/client'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$data = Join-Path $RunPath 'data'
$events = @(Get-Content (Join-Path $data 'EmberSmoke/events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$side = $events | Where-Object kind -eq 'side-check' | Select-Object -First 1
if (-not $side.detail.client -or $side.detail.server) { throw 'Ember ShouldLoad side isolation failed.' }
$serverLog = Get-Content (Join-Path $data 'Logs/server-main.log') -Raw
if ($serverLog.Contains('Ember.Mod.EmberSystem') -or $serverLog.Contains('[Ember]')) { throw 'Ember executed on the singleplayer server.' }
$commands = @($events | Where-Object kind -eq 'command')
foreach ($command in @('.ember mark smoke', '.ember health', '.ember export')) {
    if (-not ($commands | Where-Object { $_.detail.command -eq $command -and $_.detail.status -eq 'Success' })) { throw "No successful result: $command" }
}
if (-not ($commands | Where-Object { $_.detail.command -like '.ember profile *' -and $_.detail.status -eq 'Success' })) { throw 'No successful profile command.' }
$zips = @(Get-ChildItem (Join-Path $data 'Ember/exports') -Filter '*.zip')
if ($zips.Count -lt 2) { throw 'Missing profile or manual export.' }
$results = @()
foreach ($zip in $zips) {
    $destination = Join-Path $RunPath ('reports/' + $zip.BaseName)
    if (-not (Test-Path $destination)) { [System.IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $destination) }
    foreach ($required in @('report.json','llm-context.json','report.html','SUMMARY.md','ASK-AN-LLM.md','metrics.csv','frames.csv','manifest.json')) {
        if (-not (Test-Path (Join-Path $destination $required))) { throw "Missing export member $required" }
    }
    $manifest = Get-Content (Join-Path $destination 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $actual = Get-Item (Join-Path $destination $file.name)
        if ($actual.Length -ne $file.bytes -or (Get-FileHash $actual.FullName).Hash -ne $file.sha256) { throw "Manifest mismatch: $($file.name)" }
    }
    $report = Get-Content (Join-Path $destination 'report.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($report.side -ne 'client' -or $report.gameVersion -ne '1.22.7') { throw 'Incorrect report game/side.' }
    if ($report.ticks.Count -lt 30 -or $report.timeline.Count -lt 5) { throw 'Insufficient actual frame/timeline measurements.' }
    if (-not ($report.timeline | Where-Object loopRate -gt 0)) { throw 'No positive FPS observations.' }
    foreach ($id in @('game', 'emberdiagnostics', 'embersmokedriver')) {
        if (-not ($report.mods | Where-Object id -eq $id)) { throw "Missing mod inventory entry: $id" }
    }
    if (-not ($report.events | Where-Object { $_.kind -eq 'user' -and $_.message -eq 'smoke' })) { throw 'Smoke timeline marker missing.' }
    $profile = $report['profile']
    if ($report.reason -like '*profile*') {
        if (-not $profile -or $profile.samples.Count -le 0 -or $profile.frames.Count -le 0 -or $profile.traceBytes -le 0) { throw 'Profile has no actual sampled stacks.' }
        if (-not (Test-Path (Join-Path $destination 'profile.speedscope.json'))) { throw 'Missing speedscope export.' }
        $speedscope = Get-Content (Join-Path $destination 'profile.speedscope.json') -Raw | ConvertFrom-Json
        if ($speedscope.profiles.Count -le 0) { throw 'Empty speedscope profiles.' }
        if (($profile.hotspots | Measure-Object selfSamples -Sum).Sum -ne $profile.samples.Count) { throw 'Hotspot self counts do not match retained samples.' }
        if ($profile.samplesDroppedByLimit -gt 0 -and $profile.samples[-1].relativeMs -lt $profile.durationSeconds * 900) { throw 'Downsampling failed to retain the tail of the capture.' }
    }
    $fps = $report.timeline | Measure-Object loopRate -Minimum -Maximum -Average
    $results += [ordered]@{
        bundle=$zip.Name; reason=$report.reason; gameVersion=$report.gameVersion; side=$report.side
        frameRecords=$report.ticks.Count; timelineWindows=$report.timeline.Count
        fpsMin=$fps.Minimum; fpsMax=$fps.Maximum; fpsMean=$fps.Average
        mods=@($report.mods | ForEach-Object { "$($_.id)@$($_.version)" })
        renderStages=@($report.timeline[-1].renderStagesMs.Keys)
        profileSamples=$(if ($profile) { $profile.samples.Count } else { 0 })
        profileFrames=$(if ($profile) { $profile.frames.Count } else { 0 })
        profileThreads=$(if ($profile) { @($profile.samples.threadId | Sort-Object -Unique).Count } else { 0 })
        traceBytes=$(if ($profile) { $profile.traceBytes } else { 0 })
        durationSeconds=$(if ($profile) { $profile.durationSeconds } else { 0 })
        manifestVerified=$true
    }
}
if (@(Get-ChildItem $RunPath -Recurse -File -Filter '*clientsettings*').Count -gt 0) { throw 'Copied client credentials were not cleaned up.' }
# Compare known credential values privately against exported text. Never print the values.
$source = Get-Content "$env:APPDATA/VintagestoryData/clientsettings.json" -Raw | ConvertFrom-Json -AsHashtable
$secrets = @('mptoken','sessionkey','sessionsignature','useremail','entitlements') | ForEach-Object { $source.stringSettings[$_] } | Where-Object { $_ -and $_.Length -ge 6 }
foreach ($file in Get-ChildItem (Join-Path $RunPath 'reports') -Recurse -File) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($secret in $secrets) { if ($content.Contains($secret)) { throw "Credential value detected in export file: $($file.Name)" } }
}
$source = $null; $secrets = $null
$summary = [ordered]@{ verifiedUtc=[DateTimeOffset]::UtcNow; passed=$true; shouldLoadClient=$true; shouldLoadServer=$false; emberAbsentFromServerSystems=$true; copiedCredentialsRemoved=$true; exportsDoNotContainKnownCredentials=$true; reports=$results }
$summary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $RunPath 'verification.json')
$summary | ConvertTo-Json -Depth 20
