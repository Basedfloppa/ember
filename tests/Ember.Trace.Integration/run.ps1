#Requires -Version 7.4
[CmdletBinding()]
param(
    [switch] $Smoke,
    [ValidateSet('normal', 'stop', 'cancel', 'limit', 'invalid-pid')]
    [string[]] $Modes = @('normal', 'stop', 'cancel', 'limit', 'invalid-pid')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Smoke) { $Modes = @('normal', 'cancel', 'invalid-pid') }
$Modes = @($Modes | Select-Object -Unique)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $root "test-runs/trace/$runId"
$buildDirectory = Join-Path $runDirectory 'build'
$null = New-Item -ItemType Directory -Path $runDirectory -Force
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$ownedChildren = [Collections.Generic.List[Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()

function Assert-That([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Start-OwnedChild([string] $Name, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    if ($null -eq $child) { throw "Could not start $Name" }
    $ownedChildren.Add($child)
    Add-Content -LiteralPath (Join-Path $runDirectory 'processes.log') -Value "$Name pid=$($child.Id)"
    return $child
}

function Close-OwnedChild([Diagnostics.Process] $Child) {
    if ($null -eq $Child -or !$ownedChildren.Contains($Child)) { return }
    try {
        if (!$Child.HasExited) {
            # Only a process object returned by Start-OwnedChild can reach this code.
            # Include its descendants so a timed-out worker's private parser is cleaned up.
            $Child.Kill($true)
            Assert-That ($Child.WaitForExit(5000)) "Child PID $($Child.Id) did not exit after termination"
        }
    }
    finally {
        $null = $ownedChildren.Remove($Child)
        $Child.Dispose()
    }
}

function Read-BoundedLine([Diagnostics.Process] $Child, [string] $Name) {
    try {
        return $Child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    }
    catch { throw "Timed out or failed reading $Name handshake: $($_.Exception.Message)" }
}

function Read-BoundedResult($Task) {
    return $Task.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
}

function Build-Project([string] $Project, [string] $Name) {
    $child = $null
    try {
        $child = Start-OwnedChild "build-$Name" @(
            'build', $Project, '--configuration', 'Release', '--artifacts-path', $buildDirectory,
            '--disable-build-servers', '--nologo', '-maxcpucount:1', '-p:UseSharedCompilation=false'
        )
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        $child.StandardInput.Close()
        Assert-That ($child.WaitForExit(180000)) "Build timed out: $Name"
        $outText = Read-BoundedResult $stdout
        $errorText = Read-BoundedResult $stderr
        Set-Content -LiteralPath (Join-Path $runDirectory "build-$Name.stdout.log") -Value $outText
        Set-Content -LiteralPath (Join-Path $runDirectory "build-$Name.stderr.log") -Value $errorText
        Assert-That ($child.ExitCode -eq 0) "Build failed: $Name`n$outText`n$errorText"
        Write-Host "Built $Name (Release)"
    }
    finally { Close-OwnedChild $child }
}

function Assert-NoTemporaryTrace([string] $OutputDirectory) {
    if (Test-Path -LiteralPath $OutputDirectory) {
        $temporary = @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object Name -Like '.ember-trace-*')
        Assert-That ($temporary.Count -eq 0) 'Temporary EventPipe/TraceLog artifacts remain'
        $raw = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force -File | Where-Object Extension -In @('.nettrace', '.etlx'))
        Assert-That ($raw.Count -eq 0) 'A raw trace was retained'
    }
}

function Assert-Profile([string] $OutputDirectory, [string] $Mode, [int] $MaxMb) {
    $json = Get-Content -LiteralPath (Join-Path $OutputDirectory 'profile.json') -Raw
    $profile = $json | ConvertFrom-Json -Depth 100
    Assert-That ($profile.samples.Count -gt 0 -and $profile.samples.Count -le 200000) 'Invalid sample count'
    Assert-That ($profile.traceBytes -gt 0 -and $profile.traceBytes -le $MaxMb * 1024 * 1024) 'Raw trace byte bound exceeded'
    Assert-That ($profile.durationSeconds -gt 0) 'Invalid capture duration'
    Assert-That ($profile.allocations.Count -gt 0) 'No allocation types'
    Assert-That (@($profile.allocations | Where-Object { $_.sampledBytes -gt 0 -and $_.events -gt 0 }).Count -gt 0) 'No sampled allocation bytes'
    Assert-That ($profile.exceptionTypes.'System.InvalidOperationException' -gt 0) 'No expected exception type'
    Assert-That (!$json.Contains('PRIVATE_EXCEPTION_MESSAGE')) 'Exception message leaked into profile'
    Assert-That ($profile.contentionEvents -gt 0 -and $profile.contentionTotalMs -gt 0) 'No contention duration'
    $owned = @($profile.frames | Where-Object {
        $_.module -eq 'Target' -and $_.mods.Count -eq 2 -and $_.mods -contains 'mod-alpha' -and $_.mods -contains 'mod-beta'
    })
    Assert-That ($owned.Count -gt 0) 'No normalized module with both mapped owners'

    $recursiveIds = [Collections.Generic.HashSet[int]]::new()
    $mainIds = [Collections.Generic.HashSet[int]]::new()
    for ($i = 0; $i -lt $profile.frames.Count; $i++) {
        if ($profile.frames[$i].name -like '*Work.Recurse(int32)') { $null = $recursiveIds.Add($i) }
        if ($profile.frames[$i].name -like 'Program.<Main>*') { $null = $mainIds.Add($i) }
    }
    Assert-That ($recursiveIds.Count -gt 0) 'Missing full recursive method signature'
    $selfCounts = [int[]]::new($profile.frames.Count)
    $managedSelfCounts = [int[]]::new($profile.frames.Count)
    $inclusiveCounts = [int[]]::new($profile.frames.Count)
    $managedSamples = 0
    $externalSamples = 0
    $orderedStacks = 0
    $recursiveStacks = 0
    foreach ($sample in $profile.samples) {
        Assert-That ($sample.relativeMs -ge 0 -and $sample.relativeMs -le $profile.durationSeconds * 1000) 'Timestamp is not capture-relative'
        Assert-That ($sample.stack.Count -gt 0) 'Empty retained sample stack'
        $seen = [Collections.Generic.HashSet[int]]::new()
        $mainPosition = -1
        $recursivePosition = -1
        $recursiveFrames = 0
        for ($position = 0; $position -lt $sample.stack.Count; $position++) {
            [int] $frame = $sample.stack[$position]
            Assert-That ($frame -ge 0 -and $frame -lt $profile.frames.Count) 'Invalid frame reference'
            if ($seen.Add($frame)) { $inclusiveCounts[$frame]++ }
            if ($mainPosition -lt 0 -and $mainIds.Contains($frame)) { $mainPosition = $position }
            if ($recursiveIds.Contains($frame)) {
                if ($recursivePosition -lt 0) { $recursivePosition = $position }
                $recursiveFrames++
            }
        }
        if ($recursiveFrames -gt 1) { $recursiveStacks++ }
        if ($mainPosition -ge 0 -and $recursivePosition -ge 0) {
            $orderedStacks++
            Assert-That ($mainPosition -lt $recursivePosition) 'Stack is not root-first'
        }
        $leaf = $sample.stack[-1]
        $selfCounts[$leaf]++
        if ($sample.managed) { $managedSamples++; $managedSelfCounts[$leaf]++ }
        else { $externalSamples++ }
    }
    Assert-That ($managedSamples -gt 0 -and $externalSamples -gt 0) 'Missing managed/external sample classification'
    Assert-That ($orderedStacks -gt 0) 'No stack with both Main and Recurse to verify root-first order'
    Assert-That ($recursiveStacks -gt 0) 'No repeated recursive frames to exercise inclusive counting'
    $hotspotIds = [Collections.Generic.HashSet[int]]::new()
    foreach ($hotspot in $profile.hotspots) {
        [int] $frame = $hotspot.frame
        Assert-That ($frame -ge 0 -and $frame -lt $profile.frames.Count) 'Invalid hotspot frame'
        Assert-That ($hotspotIds.Add($frame)) 'Duplicate hotspot frame'
        Assert-That ($hotspot.inclusiveSamples -eq $inclusiveCounts[$frame]) "Incorrect inclusive count for frame $frame"
        Assert-That ($hotspot.selfSamples -eq $selfCounts[$frame]) "Incorrect self count for frame $frame"
        Assert-That ($hotspot.managedSelfSamples -eq $managedSelfCounts[$frame]) "Incorrect managed self count for frame $frame"
    }
    for ($i = 0; $i -lt $inclusiveCounts.Length; $i++) {
        if ($inclusiveCounts[$i] -gt 0) { Assert-That ($hotspotIds.Contains($i)) "Missing hotspot for frame $i" }
    }
    if ($Mode -eq 'stop') { Assert-That ($profile.durationSeconds -lt 4) 'Stop did not finish capture early' }
    if ($Mode -eq 'limit') { Assert-That $profile.sizeLimitReached 'Size-limit scenario did not reach its limit' }
    return [pscustomobject]@{
        mode = $Mode; status = 'passed'; samples = $profile.samples.Count; frames = $profile.frames.Count
        traceBytes = $profile.traceBytes; sizeLimitReached = $profile.sizeLimitReached
        durationSeconds = $profile.durationSeconds; contentionEvents = $profile.contentionEvents
    }
}

$target = $null
try {
    Write-Host "Trace integration artifacts: $runDirectory"
    # Keep builds sequential: the worker builds its Core project reference in the same artifacts tree.
    Build-Project (Join-Path $PSScriptRoot 'Target.csproj') 'target'
    Build-Project (Join-Path $root 'src/Ember.Trace/Ember.Trace.csproj') 'worker'
    $targetDll = Join-Path $buildDirectory 'bin/Target/release/Target.dll'
    $workerDll = Join-Path $buildDirectory 'bin/Ember.Trace/release/Ember.Trace.dll'
    Assert-That (Test-Path -LiteralPath $targetDll) "Missing target build: $targetDll"
    Assert-That (Test-Path -LiteralPath $workerDll) "Missing worker build: $workerDll"
    $map = Join-Path $runDirectory 'assembly-mod-map.json'
    Set-Content -LiteralPath $map -Value '{"Target.dll":["mod-alpha","mod-beta"]}'

    if (@($Modes | Where-Object { $_ -ne 'invalid-pid' }).Count -gt 0) {
        $target = Start-OwnedChild 'target' @($targetDll)
        $targetErrors = $target.StandardError.ReadToEndAsync()
        $targetPidLine = Read-BoundedLine $target 'target PID'
        Assert-That ($targetPidLine -match '^\d+$' -and [int]$targetPidLine -eq $target.Id) 'Target handshake did not match the process launched by this runner'
        $targetOutput = $target.StandardOutput.ReadToEndAsync()
    }

    foreach ($mode in $Modes) {
        $worker = $null
        $outputDirectory = Join-Path $runDirectory $mode
        $maxMb = if ($mode -eq 'limit') { 16 } else { 64 }
        $seconds = if ($mode -eq 'limit') { 30 } else { 5 }
        $targetPid = if ($mode -eq 'invalid-pid') { 0 } else { $target.Id }
        try {
            $worker = Start-OwnedChild "worker-$mode" @(
                $workerDll, '--pid', "$targetPid", '--seconds', "$seconds", '--output', $outputDirectory,
                '--max-mb', "$maxMb", '--map', $map
            )
            $errors = $worker.StandardError.ReadToEndAsync()
            $ready = ''
            if ($mode -ne 'invalid-pid') {
                $ready = Read-BoundedLine $worker 'worker READY'
                Assert-That ($ready -eq 'READY') "Expected READY, received '$ready'"
            }
            $stdout = $worker.StandardOutput.ReadToEndAsync()
            if ($mode -in @('stop', 'cancel')) {
                Start-Sleep -Milliseconds 700
                $worker.StandardInput.WriteLine($mode)
                $worker.StandardInput.Flush()
            }
            $worker.StandardInput.Close()
            $exitTimeoutMs = if ($mode -eq 'invalid-pid') { 15000 } elseif ($mode -eq 'cancel') { 30000 } else { 150000 }
            Assert-That ($worker.WaitForExit($exitTimeoutMs)) "Worker timed out in mode $mode"
            $stderr = Read-BoundedResult $errors
            $tail = (Read-BoundedResult $stdout).Trim()
            Set-Content -LiteralPath (Join-Path $runDirectory "$mode.stdout.log") -Value ((@($ready, $tail) | Where-Object { $_ }) -join "`n")
            Set-Content -LiteralPath (Join-Path $runDirectory "$mode.stderr.log") -Value $stderr
            Assert-NoTemporaryTrace $outputDirectory

            if ($mode -in @('cancel', 'invalid-pid')) {
                $expectedExit = if ($mode -eq 'cancel') { 2 } else { 1 }
                Assert-That ($worker.ExitCode -eq $expectedExit) "Expected exit $expectedExit, got $($worker.ExitCode): $stderr"
                Assert-That (!(Test-Path -LiteralPath (Join-Path $outputDirectory 'profile.json'))) "$mode wrote a profile"
                Assert-That ([string]::IsNullOrWhiteSpace($tail)) "$mode unexpectedly wrote a success protocol line: $tail"
                if ($mode -eq 'invalid-pid') { Assert-That ($stderr -match '--pid') 'Invalid PID failure did not explain the argument error on stderr' }
                else { Assert-That ([string]::IsNullOrWhiteSpace($stderr)) "Cancel produced an unexpected error: $stderr" }
                $result = [pscustomobject]@{ mode = $mode; status = 'passed'; exitCode = $worker.ExitCode; profileWritten = $false }
            }
            else {
                Assert-That ($worker.ExitCode -eq 0) "Worker failed with exit $($worker.ExitCode): $stderr"
                Assert-That ($tail -eq 'DONE') "Expected DONE, received '$tail'"
                Assert-That ([string]::IsNullOrWhiteSpace($stderr)) "Unexpected worker diagnostic: $stderr"
                $result = Assert-Profile $outputDirectory $mode $maxMb
            }
            $results.Add($result)
            Write-Host ('PASS ' + ($result | ConvertTo-Json -Compress))
        }
        catch {
            $results.Add([pscustomobject]@{ mode = $mode; status = 'failed'; error = $_.Exception.Message })
            throw
        }
        finally { Close-OwnedChild $worker }
    }
}
finally {
    # Reverse launch order: workers/builds first, target last. Never search for or kill other dotnet processes.
    $remaining = $ownedChildren.ToArray()
    [Array]::Reverse($remaining)
    foreach ($child in $remaining) {
        try { Close-OwnedChild $child }
        catch { Write-Warning "Owned-child cleanup failed: $($_.Exception.Message)" }
    }
    $results.ToArray() | ConvertTo-Json -Depth 10 -AsArray | Set-Content -LiteralPath (Join-Path $runDirectory 'results.json')
    Write-Host "Results: $(Join-Path $runDirectory 'results.json')"
}
