[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$GamePath = "$env:APPDATA/Vintagestory",
    [ValidateSet('manual','profile','leave','shutdown','cancel','immediate','double-start','reconnect')]
    [string[]]$Scenario = @('manual','profile','leave','shutdown','cancel','immediate','double-start','reconnect'),
    [ValidateSet('Multiplayer','Singleplayer','Both')][string]$Mode = 'Multiplayer',
    [ValidateRange(25,28000)][int]$DurationSeconds = 35,
    [ValidateRange(10,120)][int]$SegmentSeconds = 10,
    [ValidateRange(60,3600)][int]$StartupTimeoutSeconds = 240,
    [ValidateRange(60,3600)][int]$FinalizeTimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($DurationSeconds -ge 900 -and -not $PSBoundParameters.ContainsKey('SegmentSeconds')) { $SegmentSeconds = 60 }
$historySeconds = if ($DurationSeconds -ge 900) { 900 } else { 3600 }
$maxTickRecords = if ($DurationSeconds -ge 900) { 120000 } else { 500000 }
$root = Split-Path $PSScriptRoot -Parent
$Package = (Resolve-Path -LiteralPath $Package).Path
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
$run = Join-Path $root ('test-runs/session-game/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
$null = New-Item -ItemType Directory -Path $run
$owned = @{} # PID -> creation-time-checked Process; never kill by executable name.
$launched = [Collections.Generic.List[object]]::new()

function Save-Json($Value, [string]$Path) { $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
    catch [System.IO.IOException] { return $null }
}
function Start-Owned([string]$Exe, [string[]]$Arguments, [string]$LogPrefix, [hashtable]$Environment = @{}, [string]$Kind = 'client') {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.WorkingDirectory = $GamePath
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[$entry.Key] = [string]$entry.Value }
    $p = [Diagnostics.Process]::Start($info)
    $entry = @{ process=$p; started=$p.StartTime.ToUniversalTime(); kind=$Kind; stdout=$p.StandardOutput.ReadToEndAsync(); stderr=$p.StandardError.ReadToEndAsync(); prefix=$LogPrefix }
    $owned[$p.Id] = $entry
    $launched.Add($entry)
    return $p
}
function Find-OwnedChildren {
    # Poll while parents live, retaining identities across client exit for the supervisor.
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate, Name)
    do {
        $added = $false
        foreach ($candidate in $all) {
            $id = [int]$candidate.ProcessId; $parent = [int]$candidate.ParentProcessId
            if ($owned.ContainsKey($id) -or -not $owned.ContainsKey($parent)) { continue }
            if ($candidate.CreationDate.ToUniversalTime() -lt $owned[$parent].started) { continue }
            # A recycled parent PID is not an ownership relationship.
            $parentNow = $all | Where-Object ProcessId -eq $parent | Select-Object -First 1
            if ($parentNow -and [Math]::Abs(($parentNow.CreationDate.ToUniversalTime() - $owned[$parent].started).TotalSeconds) -gt 1) { continue }
            try {
                $p = Get-Process -Id $id -ErrorAction Stop
                if ([Math]::Abs(($p.StartTime.ToUniversalTime() - $candidate.CreationDate.ToUniversalTime()).TotalSeconds) -gt 1) { continue }
                $kind = if ($candidate.Name -match '^(dotnet|Ember\.Trace)(\.exe)?$') { 'helper' } else { 'child' }
                $owned[$id] = @{ process=$p; started=$p.StartTime.ToUniversalTime(); kind=$kind; parent=$parent }
                $added = $true
            } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
        }
    } while ($added)
}
function Is-Alive($Entry) {
    try { return -not $Entry.process.HasExited -and $Entry.process.StartTime.ToUniversalTime() -eq $Entry.started }
    catch { return $false }
}
function Stop-Owned($Entry) {
    if (-not (Is-Alive $Entry)) { return }
    $p = $Entry.process
    if ($Entry.kind -eq 'server') { try { $p.StandardInput.WriteLine('/stop'); $p.StandardInput.Flush() } catch {} }
    elseif ($Entry.kind -eq 'client') { $null = $p.CloseMainWindow() }
    if (-not $p.WaitForExit(15000)) {
        # Retain the process handle and verify creation time; no process-tree/name-wide kills.
        if (Is-Alive $Entry) { $p.Kill(); $p.WaitForExit() }
    }
}
function Flush-Logs {
    foreach ($entry in $launched) {
        if ($entry.process.HasExited) {
            [IO.File]::WriteAllText($entry.prefix + '.stdout.log', $entry.stdout.GetAwaiter().GetResult())
            [IO.File]::WriteAllText($entry.prefix + '.stderr.log', $entry.stderr.GetAwaiter().GetResult())
        }
    }
}

try {
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $infoEntry = $archive.GetEntry('modinfo.json')
        if (-not $infoEntry) { throw 'Package has no root modinfo.json.' }
        $reader = [IO.StreamReader]::new($infoEntry.Open())
        try { $mod = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($mod.modid -ne 'emberdiagnostics' -or $mod.version -notmatch '^1\.1\.') { throw 'An explicit Ember 1.1.x package is required.' }
    } finally { $archive.Dispose() }
    Save-Json @{ packageVersion=$mod.version; sha256=(Get-FileHash -LiteralPath $Package).Hash; gameVersion='1.22.7'; durationSeconds=$DurationSeconds; segmentSeconds=$SegmentSeconds; historySeconds=$historySeconds; maxTickRecords=$maxTickRecords; mode=$Mode; scenarios=$Scenario } (Join-Path $run 'run.json')
    $driver = Join-Path $run 'driver'
    & dotnet build (Join-Path $root 'tests/Ember.Session.GameTests/Ember.Session.GameTests.csproj') -c Release "-p:VintageStoryPath=$GamePath" -o $driver
    if ($LASTEXITCODE -ne 0) { throw 'Session test driver build failed.' }
    $modes = if ($Mode -eq 'Both') { @('Singleplayer','Multiplayer') } else { @($Mode) }
    foreach ($testMode in $modes) { foreach ($test in $Scenario) {
        $case = Join-Path $run "$testMode-$test"
        $data = Join-Path $case 'data'
        $mods = Join-Path $data 'Mods'
        $evidence = Join-Path $data 'EmberSessionTest'
        $null = New-Item -ItemType Directory -Force -Path $mods, $evidence, (Join-Path $data 'ModConfig')
        $client = $null; $server = $null
        $casePids = @($owned.Keys)
        try {
            Copy-Item -LiteralPath $driver -Destination (Join-Path $mods 'SessionDriver') -Recurse
            Copy-Item -LiteralPath $Package -Destination (Join-Path $mods ([IO.Path]::GetFileName($Package)))
            Save-Json @{ SessionSegmentSeconds=$SegmentSeconds; SessionMaxSeconds=[Math]::Max(60,$DurationSeconds+120); SessionDiskBudgetMegabytes=2048; SessionSegmentSamples=50000; OpenFolderWhenFinished=$false; EnableGpuTiming=$true; KeepExports=20; HistorySeconds=$historySeconds; MaxTickRecords=$maxTickRecords } (Join-Path $data 'ModConfig/emberdiagnostics.json')
            # Read credentials only into memory and this isolated private data directory.
            $settings = Get-Content -LiteralPath "$env:APPDATA/VintagestoryData/clientsettings.json" -Raw | ConvertFrom-Json -AsHashtable
            foreach ($pair in @{screenWidth=960;screenHeight=540;gameWindowMode=0;viewDistance=64;maxFps=60;shadowMapQuality=0;ssaoQuality=0;godRays=0;particleLevel=0;maxDynamicLights=0;cloudRenderMode=0;masterSoundLevel=0;vsyncMode=0}.GetEnumerator()) { $settings.intSettings[$pair.Key] = $pair.Value }
            foreach ($pair in @{pauseGameOnLostFocus=$false;showCreativeHelpDialog=$false;showSurvivalHelpDialog=$false;bloom=$false;fxaa=$false;wavingStuff=$false;ambientParticles=$false;multipleInstances=$true}.GetEnumerator()) { $settings.boolSettings[$pair.Key] = $pair.Value }
            $settings.floatSettings['ssaa'] = 1.0
            $settings.stringListSettings['modPaths'] = @((Join-Path $GamePath 'Mods'), $mods)
            $settings.stringListSettings['disabledMods'] = @()
            $settings.stringListSettings['multiplayerservers'] = @()
            Save-Json $settings (Join-Path $data 'clientsettings.json')
            $settings = $null
            $arguments = @('--dataPath', $data)
            if ($testMode -eq 'Multiplayer') {
                $serverData = Join-Path $case 'server-data'
                $null = New-Item -ItemType Directory -Force -Path $serverData, (Join-Path $serverData 'Mods')
                # These names were inspected on VS1.22.7 ServerProgramArgs; no real server config is read.
                $serverExe = Join-Path $GamePath 'VintagestoryServer.exe'
                $generator = Start-Owned $serverExe @('--dataPath',$serverData,'--genconfig') (Join-Path $case 'server-config') @{} 'server'
                if (-not $generator.WaitForExit(30000)) { throw 'Isolated server config generation timed out.' }
                if ($generator.ExitCode -ne 0) { throw 'Isolated server config generation failed.' }
                $portReservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
                $portReservation.Start()
                try { $port = $portReservation.LocalEndpoint.Port } finally { $portReservation.Stop() }
                $configPath = Join-Path $serverData 'serverconfig.json'
                $config = Read-Json $configPath
                if (-not $config) { throw 'VS did not generate a server config.' }
                foreach ($pair in @{Ip='127.0.0.1';Port=$port;Upnp=$false;AdvertiseServer=$false;WhitelistMode='Off';VerifyPlayerAuth=$false;DefaultRoleCode='suplayer';MaxClients=2;MaxChunkRadius=4;MapSizeX=1024;MapSizeY=128;MapSizeZ=1024;BlockTickChunkRange=2;PassTimeWhenEmpty=$false;ServerName='Ember isolated loopback test'}.GetEnumerator()) { $config[$pair.Key]=$pair.Value }
                $config.ModPaths = @((Join-Path $GamePath 'Mods'), (Join-Path $serverData 'Mods'))
                # Refuse unexpected installed server mods instead of loading a user's additions.
                $unexpected = @(Get-ChildItem (Join-Path $GamePath 'Mods') -File | Where-Object { $_.Extension -in '.dll','.zip' -and $_.Name -notin 'VSEssentials.dll','VSSurvivalMod.dll','VSCreativeMod.dll' })
                if ($unexpected.Count) { throw 'Game Mods contains additional mods; server isolation cannot be guaranteed.' }
                $config.WorldConfig.SaveFileLocation = Join-Path $serverData 'Saves/session-test.vcdbs'
                $config.WorldConfig.WorldName = 'Ember loopback session test'
                Save-Json $config $configPath
                $server = Start-Owned $serverExe @('--dataPath',$serverData,'--ip','127.0.0.1','--port',"$port",'--maxclients','2','--reducedthreads') (Join-Path $case 'server') @{} 'server'
                $readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
                do {
                    if ($server.HasExited) { throw 'Isolated dedicated server exited before readiness.' }
                    if ([DateTimeOffset]::UtcNow -gt $readyDeadline) { throw 'Isolated dedicated server readiness timed out.' }
                    $tcp = [Net.Sockets.TcpClient]::new()
                    try { $tcp.Connect('127.0.0.1',$port); $ready=$true } catch { $ready=$false } finally { $tcp.Dispose() }
                    if (-not $ready) { Start-Sleep -Milliseconds 500 }
                } while (-not $ready)
                Save-Json @{ip='127.0.0.1';port=$port;pid=$server.Id;defaultRole='suplayer';verifyPlayerAuth=$false;note='Loopback transport/role compatibility, not external authentication service coverage';emberInstalled=$false} (Join-Path $case 'server-isolation.json')
                $arguments += @('--connect',"127.0.0.1:$port")
            } else { $arguments += @('--openWorld','EmberSessionTest','--playStyle','creativebuilding') }
            $client = Start-Owned (Join-Path $GamePath 'Vintagestory.exe') $arguments (Join-Path $case 'client') @{EMBER_SESSION_SCENARIO=$test;EMBER_SESSION_DURATION="$DurationSeconds"}
            Save-Json @{pid=$client.Id;startedUtc=[DateTimeOffset]::UtcNow;scenario=$test;mode=$testMode;durationSeconds=$DurationSeconds;segmentSeconds=$SegmentSeconds;historySeconds=$historySeconds;maxTickRecords=$maxTickRecords} (Join-Path $case 'launch.json')
            $worlds = if ($test -eq 'reconnect') { 2 } else { 1 }
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(($StartupTimeoutSeconds + $DurationSeconds + 25) * $worlds + $FinalizeTimeoutSeconds)
            $closed = $false; $completed = $false; $closeDeadline = $null
            while ([DateTimeOffset]::UtcNow -lt $deadline) {
                Find-OwnedChildren
                $status = Read-Json (Join-Path $evidence 'status.json')
                if ($status -and $status.phase -eq 'failed') { throw 'Session driver failed; inspect private evidence.' }
                if ($status -and $status.phase -eq 'close-requested' -and -not $closed) {
                    Find-OwnedChildren
                    if (-not $client.CloseMainWindow()) { throw 'Could not request graphical client window close.' }
                    $closed = $true; $closeDeadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
                    Save-Json @{requestedUtc=[DateTimeOffset]::UtcNow;pid=$client.Id} (Join-Path $case 'graceful-close.json')
                }
                if ($closed -and -not $client.HasExited -and [DateTimeOffset]::UtcNow -gt $closeDeadline) { throw 'Graceful graphical shutdown did not complete.' }
                if ($client.HasExited -and -not $closed) { throw 'Graphical client exited unexpectedly.' }
                $sessions = @(Get-ChildItem (Join-Path $data 'Ember/sessions') -Directory -ErrorAction SilentlyContinue)
                if ($sessions.Count -gt $worlds) { throw 'Duplicate start or leaked world created extra recording directories.' }
                $results = @($sessions | Where-Object { Test-Path (Join-Path $_.FullName 'result.json') })
                $terminal = $status -and $status.generation -eq $worlds -and $status.phase -in 'finalizing','leaving','close-requested'
                if ($terminal -and $results.Count -eq $worlds -and (-not $closed -or $client.HasExited)) {
                    $liveHelpers = @($owned.Values | Where-Object { $_.kind -eq 'helper' -and (Is-Alive $_) })
                    if ($liveHelpers.Count -eq 0) { $completed=$true; break }
                }
                Start-Sleep -Milliseconds 250
            }
            if (-not $completed) { throw 'Session scenario timed out waiting for durable results and helper exit.' }
            if ($test -eq 'shutdown') {
                Save-Json @{exitUtc=$client.ExitTime.ToUniversalTime();exitCode=$client.ExitCode;helpersExited=$true} (Join-Path $case 'client-exit.json')
            }
            # Allow late duplicate exports/callbacks to become observable before verification.
            Start-Sleep -Seconds 3
            Find-OwnedChildren
            Save-Json @($owned.GetEnumerator() | Where-Object { $_.Key -notin $casePids } | ForEach-Object { @{pid=$_.Key;startedUtc=$_.Value.started;kind=$_.Value.kind;exited=(-not (Is-Alive $_.Value))} }) (Join-Path $case 'processes-before-cleanup.json')
        } finally {
            try {
                try { Find-OwnedChildren } catch { }
                if ($client) { Stop-Owned $owned[$client.Id] }
                if ($server) { Stop-Owned $owned[$server.Id] }
                foreach ($entry in @($owned.Values)) { if (Is-Alive $entry) { Stop-Owned $entry } }
                Flush-Logs
            } finally {
                # The game can create settings backups during any shutdown path.
                Get-ChildItem -LiteralPath $case -Recurse -File -Filter '*clientsettings*' -ErrorAction SilentlyContinue | Remove-Item -Force
            }
        }
        & (Join-Path $root 'tests/Ember.Session.GameTests/verify.ps1') -RunPath $case
        Write-Host "Session scenario verified: $testMode/$test ($case)"
    } }
    Write-Host "Graphical session suite completed: $run"
} finally {
    # Even failed builds/config generation/first launch attempts follow the same cleanup.
    try { Find-OwnedChildren } catch { }
    foreach ($entry in @($owned.Values)) { try { Stop-Owned $entry } catch { } }
    try { Flush-Logs } catch { }
    Get-ChildItem -LiteralPath $run -Recurse -File -Filter '*clientsettings*' -ErrorAction SilentlyContinue | Remove-Item -Force
}
