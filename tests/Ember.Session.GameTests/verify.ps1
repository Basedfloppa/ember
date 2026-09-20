[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Load([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Unix($Value) {
    if ($Value -is [DateTimeOffset]) { return $Value.ToUnixTimeMilliseconds() }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToUnixTimeMilliseconds() }
    return ([DateTimeOffset]::Parse([string]$Value)).ToUnixTimeMilliseconds()
}
function Summary($Values) {
    $a = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    if (-not $a.Count) { return @{count=0} }
    return @{count=$a.Count;mean=($a | Measure-Object -Average).Average;p50=$a[[Math]::Max(0,[int][Math]::Ceiling($a.Count*.5)-1)];p95=$a[[Math]::Max(0,[int][Math]::Ceiling($a.Count*.95)-1)];max=$a[-1]}
}
$RunPath = (Resolve-Path -LiteralPath $RunPath).Path
$data = Join-Path $RunPath 'data'
$launch = Load (Join-Path $RunPath 'launch.json')
$scenario = $launch['scenario']
$segmentSeconds = if ($launch.ContainsKey('segmentSeconds')) { [int]$launch.segmentSeconds } else { 10 } # Earlier lifecycle runs used fixed ten-second segments.
$worlds = if ($scenario -eq 'reconnect') { 2 } else { 1 }
$events = @(Get-Content (Join-Path $data 'EmberSessionTest/events.ndjson') | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
Require (-not @($events | Where-Object { $_['kind'] -eq 'driver-error' }).Count) 'Driver reported an error.'
$loaded = @($events | Where-Object { $_['kind'] -eq 'world-loaded' })
Require ($loaded.Count -eq $worlds) 'Incorrect loaded-world count; reconnect did not complete or callbacks leaked.'
foreach ($world in $loaded) {
    Require ($world.detail.emberClient -eq $true -and $world.detail.emberServer -eq $false) 'Ember client/server side isolation failed.'
    Require ($world.detail.singleplayer -eq ($launch.mode -eq 'Singleplayer')) 'Actual game multiplayer state does not match launch mode.'
    if ($launch.mode -eq 'Multiplayer') {
        Require ($world.detail.role -eq 'suplayer') 'Dedicated-server test did not join as the regular survival player role.'
        Require (-not @($world.detail.privileges | Where-Object { $_ -in '*','controlserver','ban','kick','give','gamemode' }).Count) 'Dedicated-server player has elevated privileges.'
    }
}
$commands = @($events | Where-Object { $_['kind'] -eq 'command' })
$starts = @($commands | Where-Object { $_.detail.command -in '.ember start','.ember profile' })
$expectedStarts = if ($scenario -eq 'double-start') { 2 } else { $worlds }
Require ($starts.Count -eq $expectedStarts) 'Incorrect start command count (duplicate user flow or missing command).'
Require ($starts[0].detail.status -eq 'Success') 'Initial start command failed.'
if ($scenario -eq 'profile') { Require ($starts[0].detail.command -eq '.ember profile') 'Profile alias was not tested without seconds.' }
if ($scenario -in 'leave','shutdown') {
    Require (-not @($commands | Where-Object { $_.detail.command -in '.ember stop','.ember cancel' }).Count) 'Automatic finalization test sent a stop/cancel command.'
}
if ($scenario -eq 'reconnect') {
    Require (@($events | Where-Object { $_.kind -eq 'driver-dispose' }).Count -eq 2) 'Reconnect leaked or duplicated driver lifetime.'
    Require (@($commands | Where-Object { $_.detail.command -eq '.ember stop' }).Count -eq 1) 'Reconnect should auto-finalize first world and manually stop only the second.'
}
if ($scenario -eq 'leave') {
    Require (@($events | Where-Object { $_.kind -eq 'leave-world' }).Count -eq 1) 'Normal leave did not fire LeaveWorld exactly once.'
    $clientLog = Get-Content (Join-Path $data 'Logs/client-main.log') -Raw
    Require ($clientLog -match 'Exiting current game to main menu') 'Game did not return to its main menu.'
}
$processes = @(Load (Join-Path $RunPath 'processes-before-cleanup.json'))
Require (-not @($processes | Where-Object { $_.kind -eq 'helper' -and -not $_.exited }).Count) 'A worker was still running before cleanup.'
$sessions = @(Get-ChildItem (Join-Path $data 'Ember/sessions') -Directory | Sort-Object CreationTimeUtc)
Require ($sessions.Count -eq $worlds) 'Concurrent/extra recording directories found.'
$zips = @(Get-ChildItem (Join-Path $data 'Ember/exports') -Filter '*.zip' -ErrorAction SilentlyContinue)
Require ($zips.Count -eq $(if ($scenario -eq 'cancel') { 0 } else { $worlds })) 'Wrong number of final report ZIPs (cancel exported or duplicate finalization).'
$reports = @()
$previousStop = 0L
for ($i=0; $i -lt $sessions.Count; $i++) {
    $directory = $sessions[$i].FullName
    # Finalized sessions retain the durable outcome; successful finalization may remove
    # seed/journal/final snapshots after their evidence has been committed to the report.
    foreach ($name in 'request.json','session.json','result.json') {
        Require (Test-Path (Join-Path $directory $name)) "Missing durable session artifact: $name"
    }
    $request = Load (Join-Path $directory 'request.json')
    $session = Load (Join-Path $directory 'session.json')
    $result = Load (Join-Path $directory 'result.json')
    Require ($result.status -in 'completed','cancelled') 'Session has not reached a successful terminal result.'
    $rawTemps = @(Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {
        $_.Name -match '\.(nettrace|etl|etlx|tmp|partial)$'
    })
    Require ($rawTemps.Count -eq 0) 'Finalized session retained raw trace/parser temporary files.'
    $segmentDirectory = Join-Path $directory 'segments'
    if (Test-Path -LiteralPath $segmentDirectory) {
        Require (@(Get-ChildItem -LiteralPath $segmentDirectory -Recurse -File).Count -eq 0) 'Finalized session retained temporary segment files.'
    }
    Require ($request.processId -eq $launch.pid) 'Session targets a different game process.'
    Require ($request.segmentSeconds -eq $segmentSeconds -and $request.maxSeconds -ge 60 -and $request.maxDiskMegabytes -ge 128 -and $request.segmentSamples -ge 10000 -and -not $request.openFolderWhenFinished) 'Session config did not reach the worker.'
    Require (@($processes | Where-Object { $_.pid -eq $session.supervisorProcessId -and $_.kind -eq 'helper' -and $_.exited }).Count -eq 1) 'Supervisor ownership/exit was not observed.'
    if ($scenario -eq 'cancel') {
        Require ($result.status -eq 'cancelled' -and -not $result['exportPath']) 'Cancel did not finish without export.'
        continue
    }
    Require ($result.status -eq 'completed' -and -not $result['error']) 'Session worker failed to export successfully.'
    $zipPath = [IO.Path]::GetFullPath($result.exportPath)
    Require ($zipPath.StartsWith([IO.Path]::GetFullPath((Join-Path $data 'Ember/exports')) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Report ZIP is outside the isolated export directory.'
    Require (Test-Path -LiteralPath $zipPath) 'Final report ZIP is missing.'
    $destination = Join-Path $RunPath ('reports/' + $request.id)
    $null = New-Item -ItemType Directory -Force -Path $destination
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            Require ($target.StartsWith([IO.Path]::GetFullPath($destination) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Unsafe ZIP member path.'
            if (-not $entry.Name) { $null = New-Item -ItemType Directory -Force -Path $target; continue }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $zip.Dispose() }
    foreach ($name in 'report.json','manifest.json','SUMMARY.md','report.html','frames.csv','metrics.csv') {
        Require (Test-Path (Join-Path $destination $name)) "Missing report member: $name"
    }
    $manifest = Load (Join-Path $destination 'manifest.json')
    foreach ($file in $manifest.files) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $file.name))
        Require ($target.StartsWith([IO.Path]::GetFullPath($destination) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Unsafe manifest path.'
        Require ((Get-Item -LiteralPath $target).Length -eq $file.bytes -and (Get-FileHash -LiteralPath $target).Hash -eq $file.sha256) 'Report manifest hash/size mismatch.'
    }
    $report = Load (Join-Path $destination 'report.json')
    Require ($report.gameVersion -eq '1.22.7' -and $report.side -eq 'client') 'Wrong game version or report side.'
    Require ($report.environment.singleplayer -eq ($launch.mode -eq 'Singleplayer')) 'Report does not describe actual multiplayer/singleplayer state.'
    $info = $report.session
    $begin = Unix $info.startedUtc; $end = Unix $info.stoppedUtc
    Require ($begin -ge $previousStop) 'World session intervals overlap.'
    $previousStop = $end
    $genEvents = @($events | Where-Object { $_.generation -eq ($i+1) })
    $startEvent = @($genEvents | Where-Object { $_.kind -eq 'capture-start-request' })
    $endEvent = @($genEvents | Where-Object { $_.kind -eq 'capture-end-request' })
    Require ($startEvent.Count -eq 1 -and $endEvent.Count -eq 1) 'Missing/duplicate driver interval boundary.'
    Require ([Math]::Abs($begin - (Unix $startEvent[0].utc)) -le 5000) 'Session start differs from actual command interval.'
    Require ([Math]::Abs($end - (Unix $endEvent[0].utc)) -le 10000) 'Session stop differs from actual leave/stop/close interval.'
    if ($scenario -ne 'immediate') {
        Require (Test-Path (Join-Path $destination 'profile.speedscope.json')) 'Missing actual stack speedscope export.'
        Require (($end-$begin)/1000 -ge $launch.durationSeconds-2) 'Session ended before the requested indefinite interval.'
        $timeline = @($report.timeline | Sort-Object unixMs)
        $frames = @($report.ticks | Sort-Object endUnixMs)
        Require ($timeline.Count -ge 15 -and $frames.Count -ge 30) 'Insufficient real frame/timeline data.'
        Require ($timeline[0].unixMs -le $begin+3000 -and $timeline[-1].unixMs -ge $end-3000) 'Timeline does not cover session start and end.'
        Require ($frames[0].endUnixMs -le $begin+3000 -and $frames[-1].endUnixMs -ge $end-3000) 'Frame records do not cover session start and end.'
        for ($w=1; $w -lt $timeline.Count; $w++) { Require ($timeline[$w].unixMs-$timeline[$w-1].unixMs -le 5000) 'Unexplained gap in session timeline.' }
        if ($launch.durationSeconds -ge 900) {
            Require (@($frames | Where-Object { $_.endUnixMs -le $begin+10000 }).Count -ge 15) 'Soak lost the first frame window.'
            Require (@($frames | Where-Object { $_.endUnixMs -ge $end-10000 }).Count -ge 15) 'Soak lost the last frame window.'
        }
        $segments = @($info.segments | Sort-Object index)
        Require ($segments.Count -ge 3) 'Fewer than two segment rotations occurred.'
        Require (@($segments | Where-Object { $_.status -eq 'completed' }).Count -ge 2) 'Too few completed stack segments.'
        $coverageSeconds = [double]$info.stackCoverageSeconds
        $coveragePercent = [double]$info.stackCoveragePercent
        Require ([double]::IsFinite($coverageSeconds) -and $coverageSeconds -gt 0 -and $coverageSeconds -le $info.recordedSeconds+0.01) 'Invalid recorded stack coverage duration.'
        Require ([double]::IsFinite($coveragePercent) -and $coveragePercent -gt 0 -and $coveragePercent -le 100) 'Invalid recorded stack coverage percentage.'
        Require ([Math]::Abs($coveragePercent - 100*$coverageSeconds/$info.recordedSeconds) -le 0.01) 'Stack coverage percentage does not match covered duration.'
        # Journal coverage is checked independently above. Attach/rundown gaps must not
        # silently receive stack coverage credit merely because frames exist there.
        $coveredUpperBoundMs = 0L; $cursor = $begin
        foreach ($segment in @($segments | Where-Object { $_.status -eq 'completed' } | Sort-Object startedUtc)) {
            $a = [Math]::Max($cursor, (Unix $segment.startedUtc))
            $b = [Math]::Min($end, (Unix $segment.endedUtc))
            if ($b -gt $a) { $coveredUpperBoundMs += $b-$a; $cursor = $b }
        }
        Require ($coverageSeconds*1000 -le $coveredUpperBoundMs+10) 'Worker credited stack coverage outside completed segment intervals.'
        if ($coveragePercent -lt 99.99) {
            Require (@($info.notes | Where-Object { $_ -match '(?i)gap|no stack coverage|coverage.*(attach|rundown)' }).Count -gt 0) 'Incomplete stack coverage has no explicit gap explanation.'
        }
        # Capturing may overlap parsing, but two capture intervals may not overlap.
        for ($s=1; $s -lt $segments.Count; $s++) {
            Require ((Unix $segments[$s].startedUtc) -ge (Unix $segments[$s-1].endedUtc)-250) 'Concurrent stack recorder capture intervals overlap.'
        }
        $spike = @($genEvents | Where-Object { $_.kind -eq 'spike-begin' })
        Require ($spike.Count -eq 1) 'Main-thread test spike was not injected exactly once.'
        $spikeMs = Unix $spike[0].utc
        Require (@($frames | Where-Object { $_.endUnixMs -ge $spikeMs -and $_.endUnixMs -le $spikeMs+3000 -and $_.intervalMs -ge 100 }).Count -gt 0) 'Injected stall was not observed in actual frames.'
        Require (@($report.events | Where-Object { $_.kind -eq 'user' -and $_.message -eq 'session-test-spike' }).Count -eq 1) 'Spike timeline marker missing or duplicated.'
        $profile = $report.profile
        Require ($profile -and @($profile.samples).Count -gt 0 -and $profile.traceBytes -gt 0) 'No real sampled stacks.'
        $spikeRelative = $spikeMs - (Unix $profile.startedUtc)
        Require (@($profile.samples | Where-Object { $_.relativeMs -lt $spikeRelative-1000 }).Count -gt 0) 'No stack samples before injected spike.'
        Require (@($profile.samples | Where-Object { $_.relativeMs -gt $spikeRelative+1000 }).Count -gt 0) 'No stack samples after injected spike.'
        $gpu = @($timeline | Where-Object { $_['gpuElapsedMs'] -and $_.gpuElapsedMs.count -gt 0 })
        foreach ($window in $gpu) { foreach ($key in 'mean','p50','p95','p99','max') {
            Require ([double]::IsFinite([double]$window.gpuElapsedMs[$key]) -and [double]$window.gpuElapsedMs[$key] -ge 0) 'Invalid actual GPU elapsed measurement.'
        } }
        $gpuStatus = if ($gpu.Count) { 'measured' } else { 'explicitly-unavailable' }
        if (-not $gpu.Count) {
            $gpuEvidence = (@($report.events | Where-Object { $_.kind -match 'gpu' -and $_.message -match 'unavailable|unsupported|failed|disabled|no .*context' }) | ConvertTo-Json -Depth 10)
            $environmentEvidence = $report.environment | ConvertTo-Json -Depth 20
            Require ($gpuEvidence -or $environmentEvidence -match '(?is)gpu[^\r\n]*(unavailable|unsupported|failed|disabled|no .*context)') 'GPU timings absent without explicit runtime unavailability evidence.'
        }
        $perf = Load (Join-Path $data "EmberSessionTest/performance-$($i+1).json")
        Require ($perf.baseline.count -ge 30 -and $perf.recording.count -ge 30 -and @($perf.baselineCollector).Count -ge 5) 'Missing descriptive baseline/recording performance observations.'
        $reports += @{id=$request.id;seconds=($end-$begin)/1000;segments=$segments.Count;stackCoverageSeconds=$coverageSeconds;stackCoveragePercent=$coveragePercent;stackGapSeconds=[Math]::Max(0.0,[double]$info.recordedSeconds-$coverageSeconds);coverageNotes=$info.notes;timelineWindows=$timeline.Count;frames=$frames.Count;stackSamples=@($profile.samples).Count;gpu=$gpuStatus;gpuWindows=$gpu.Count;baselineFrameIntervals=$perf.baseline;recordingFrameIntervals=$perf.recording;baselineCollectorMs=(Summary $perf.baselineCollector.collectorMs);recordingCollectorMs=(Summary $timeline.collectorMs);comparisonNote=$perf.note}
    } else {
        Require (($end-$begin) -lt 10000) 'Immediate stop race did not promptly finalize.'
        $reports += @{id=$request.id;seconds=($end-$begin)/1000;note='Immediate-stop race: zero stack/frame samples are allowed; durable completion and a single export are required.'}
    }
    if ($scenario -eq 'shutdown') {
        $exit = Load (Join-Path $RunPath 'client-exit.json')
        Require ($exit.exitCode -eq 0) 'Graceful graphical client exit was not successful.'
        Require ((Unix $result.completedUtc) -ge (Unix $exit.exitUtc)) 'Export finished before game exit; this attempt did not demonstrate post-exit supervisor completion.'
    }
}
if ($launch.mode -eq 'Multiplayer') {
    $serverData = Join-Path $RunPath 'server-data'
    Require (-not @(Get-ChildItem (Join-Path $serverData 'Mods') -Recurse -File).Count) 'Dedicated server mod directory is not empty.'
    $config = Load (Join-Path $serverData 'serverconfig.json')
    Require ($config.Ip -eq '127.0.0.1' -and -not $config.AdvertiseServer -and -not $config.Upnp -and $config.DefaultRoleCode -eq 'suplayer') 'Server isolation/ordinary-role configuration changed.'
    $serverLogs = Get-ChildItem (Join-Path $serverData 'Logs') -Filter 'server*.log'
    Require (@($serverLogs).Count -gt 0) 'Missing dedicated-server logs.'
    foreach ($log in $serverLogs) {
        Require (-not (Select-String -LiteralPath $log.FullName -Pattern 'emberdiagnostics|embersessiontestdriver|Ember\.Mod|\[Ember\]' -Quiet)) 'Ember or its test driver was present in the dedicated server.'
    }
}
Require (-not @(Get-ChildItem -LiteralPath $RunPath -Recurse -File -Filter '*clientsettings*').Count) 'Copied credential settings/backups remain after cleanup.'
# Compare known secrets privately; never serialize their values or include them in errors.
$source = Load "$env:APPDATA/VintagestoryData/clientsettings.json"
$secrets = @('mptoken','sessionkey','sessionsignature','useremail','entitlements') | ForEach-Object { $source.stringSettings[$_] } | Where-Object { $_ -and $_.Length -ge 6 }
try {
    foreach ($file in Get-ChildItem (Join-Path $RunPath 'reports') -Recurse -File -ErrorAction SilentlyContinue) {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($secret in $secrets) { Require (-not $text.Contains($secret)) 'A known credential appeared in an exported report.' }
    }
} finally { $source=$null; $secrets=$null; $text=$null }
$summary = @{passed=$true;scenario=$scenario;mode=$launch.mode;verifiedUtc=[DateTimeOffset]::UtcNow;worlds=$worlds;helpersExited=$true;credentialsRemoved=$true;reports=$reports}
$summary | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $RunPath 'verification.json')
$summary | ConvertTo-Json -Depth 30
