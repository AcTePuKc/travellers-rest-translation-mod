# Runs one unattended profiler test against the real game.
#
# Why this exists: verifying the profiler used to mean asking a person to launch the
# game, wait, and report back, which is slow and produced several rounds of "the plugin
# looks dead but we do not know why". This does the whole cycle itself and returns a
# machine-readable verdict, so a change can be proven in about a minute.
#
# It deploys the build, launches the game, waits, reads the profiler's own report,
# checks the things that are supposed to be true, and always kills the game afterwards.

[CmdletBinding()]
param(
    # Where Travellers Rest is installed, if not the usual Steam location. The profiler
    # itself has no opinion about this; only this test script needs to know.
    [string]$GameRoot = '',

    # How long to leave the game running after the replay. The memory baseline lands 45 s
    # after the game becomes interactive.
    [int]$Seconds = 40,

    # Give up waiting for the interactive marker after this long. The game writes a marker
    # file the moment it is interactive, so this is only an upper bound.
    [int]$MenuWait = 120,

    # Replay a recorded input recipe after the game is interactive.
    [switch]$Replay,

    # Recipe to replay. Defaults to the recording in tools\load-automation.
    [string]$Recipe = '',

    # Leave the game running at the end instead of killing it.
    [switch]$KeepRunning,

    # Emit the raw report even when every check passes.
    [switch]$ShowReport,

    # Read a previous report instead of launching anything.
    [string]$InspectReport
)

$ErrorActionPreference = 'Stop'

$scriptsRoot = Split-Path -Parent $PSCommandPath
$modRoot     = Split-Path -Parent $scriptsRoot
$toolsRoot   = Join-Path $modRoot 'tools\load-automation'

if (-not $GameRoot) {
    $GameRoot = $env:TRAVELLERS_REST_DIR
}
if (-not $GameRoot) {
    # The usual Steam default. Override with -GameRoot or the TRAVELLERS_REST_DIR
    # environment variable when the game is installed on another drive.
    $GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Travellers Rest\Windows'
}
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'TravellersRest.exe'))) {
    throw ("TravellersRest.exe not found in '$GameRoot'. Pass -GameRoot, or set the " +
           "TRAVELLERS_REST_DIR environment variable.")
}
$exePath     = Join-Path $GameRoot 'TravellersRest.exe'
$pluginDir   = Join-Path $GameRoot 'BepInEx\plugins\TravellersRestProfiler'
$pluginDll   = Join-Path $pluginDir 'TravellersRestProfiler.dll'
$configPath  = Join-Path $GameRoot 'BepInEx\config\actepukc.travellersrest.profiler.cfg'
$reportDir   = Join-Path $GameRoot 'BepInEx\TravellersRestProfiler'
$reportPath  = Join-Path $reportDir 'profiler-report-latest.txt'
$logPath     = Join-Path $GameRoot 'BepInEx\LogOutput.log'
$buildDll    = Join-Path $modRoot 'src\TravellersRestProfiler\bin\Release\net472\TravellersRestProfiler.dll'
$project     = Join-Path $modRoot 'src\TravellersRestProfiler\TravellersRestProfiler.csproj'

$results = [ordered]@{}

function Say([string]$message, [string]$colour = 'Gray') {
    Write-Host $message -ForegroundColor $colour
}

function Check([string]$name, [bool]$passed, [string]$detail = '') {
    $results[$name] = $passed
    $mark = if ($passed) { 'PASS' } else { 'FAIL' }
    $colour = if ($passed) { 'Green' } else { 'Red' }
    Say ("  [{0}] {1}{2}" -f $mark, $name, $(if ($detail) { "  - $detail" } else { '' })) $colour
}

# Waiting without feedback is indistinguishable from being stuck. This ran for minutes at a
# time with nothing on screen, which is exactly as confusing as it sounds. Everything that
# waits now says what it is waiting for, how long it has been, and how much longer it will
# allow at most.
$overallTimer = [System.Diagnostics.Stopwatch]::StartNew()

function Wait-WithProgress {
    param(
        [string]$What,
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [int]$PollMs = 400
    )

    $start = [System.Diagnostics.Stopwatch]::StartNew()
    $deadline = $TimeoutSeconds * 1000
    $lastReport = -1
    while ($start.Elapsed.TotalMilliseconds -lt $deadline) {
        if (& $Condition) {
            Say ("  {0}: ready after {1:N0}s" -f $What, $start.Elapsed.TotalSeconds) 'Green'
            return $true
        }
        $whole = [int]$start.Elapsed.TotalSeconds
        # A progress line every 5 seconds: often enough to show it is alive, rare enough not
        # to bury the rest of the output.
        if ($whole -ge 5 -and $whole % 5 -eq 0 -and $whole -ne $lastReport) {
            $lastReport = $whole
            Say ("    ... waiting for {0} ({1}s of {2}s max)" -f $What, $whole, $TimeoutSeconds) 'DarkGray'
        }
        Start-Sleep -Milliseconds $PollMs
    }

    Say ("  {0}: not ready after {1}s" -f $What, $TimeoutSeconds) 'Yellow'
    return $false
}

function Stop-Game {
    $procs = Get-Process -Name 'TravellersRest' -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        # Enough for the process and its log handles to go, and no more. The game's own
        # Exit path is never used, because quitting through the menus costs another 10-15 s
        # and measures the game's shutdown rather than anything this test checks.
        Start-Sleep -Seconds 2
    }
    return -not (Get-Process -Name 'TravellersRest' -ErrorAction SilentlyContinue)
}

# ---------------------------------------------------------------- inspect only
if ($InspectReport) {
    if (-not (Test-Path -LiteralPath $InspectReport)) { throw "Report not found: $InspectReport" }
    Get-Content -LiteralPath $InspectReport -Encoding UTF8
    exit 0
}

Say ''
Say '=== Travellers Rest Profiler: unattended test ===' 'Cyan'
Say ("  duration {0}s   replay input: {1}" -f $Seconds, [bool]$Replay)

# ---------------------------------------------------------------- build
Say ''
Say 'Build' 'Cyan'
$buildOutput = & dotnet build $project -c Release -v minimal 2>&1
$buildErrors = ($buildOutput | Select-String -Pattern ': error ').Count
Check 'builds with no errors' ($buildErrors -eq 0) "$buildErrors error(s)"
if ($buildErrors -gt 0) {
    $buildOutput | Select-String -Pattern ': error ' | ForEach-Object { Say ("    " + $_.Line.Trim()) 'Red' }
    Say ''
    Say 'Build failed; nothing was deployed.' 'Red'
    exit 1
}

# ---------------------------------------------------------------- deploy
Say ''
Say 'Deploy' 'Cyan'
$stillRunning = Stop-Game
Check 'no game process before deploy' $stillRunning

Copy-Item -LiteralPath $buildDll -Destination $pluginDll -Force
$builtHash = (Get-FileHash $buildDll -Algorithm SHA256).Hash
$deployedHash = (Get-FileHash $pluginDll -Algorithm SHA256).Hash
Check 'deployed DLL matches the build' ($builtHash -eq $deployedHash)

# A stale config would silently keep old defaults, so it is always regenerated.
if (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath -Force }
if (Test-Path -LiteralPath $reportDir) { Remove-Item -LiteralPath $reportDir -Recurse -Force }
Check 'reports cleared before the run' (-not (Test-Path -LiteralPath $reportDir))

# BepInEx TRUNCATES and rewrites LogOutput.log on every launch, and its lines carry no
# timestamps at all, so neither counting lines nor filtering by time can separate one run
# from the next. The log is therefore moved aside before launching, which guarantees the
# file afterwards contains this run and nothing else. Finding this cost a run that
# reported the plugin as absent while its report sat on disk proving otherwise.
if (Test-Path -LiteralPath $logPath) {
    $archive = Join-Path $GameRoot ("BepInEx\LogOutput-previous-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Move-Item -LiteralPath $logPath -Destination $archive -Force
    Say ("  previous log archived as {0}" -f (Split-Path $archive -Leaf))
}

# ---------------------------------------------------------------- run
Say ''
Say ("Run ({0}s)" -f $Seconds) 'Cyan'
$game = Start-Process -FilePath $exePath -WorkingDirectory $GameRoot -PassThru
$runStartedAt = Get-Date
Say ("  started pid {0} at {1}" -f $game.Id, (Get-Date -Format 'HH:mm:ss'))

$replayed = $false
if ($Replay) {
    if (-not $Recipe) {
        # Pick the recording that matches the menu the game will actually show. Mod Config
        # Menu adds a Mods entry, which shifts every other button by about 78 px, so a
        # recording made with it misses every control without it. Choosing the wrong one
        # does not fail loudly: the replay clicks nothing and the run measures an idle menu.
        $mcmDll = Join-Path $GameRoot 'BepInEx\plugins\Mod Config Menu\ModConfigMenu.dll'
        $mcmFlat = Join-Path $GameRoot 'BepInEx\plugins\ModConfigMenu.dll'
        $hasMcm = (Test-Path -LiteralPath $mcmDll) -or (Test-Path -LiteralPath $mcmFlat)
        $name = if ($hasMcm) { 'load-save-modded.txt' } else { 'load-save-nomcm.txt' }
        $Recipe = Join-Path $toolsRoot $name
        Say ("  Mod Config Menu {0}; using {1}" -f $(if ($hasMcm) { 'present' } else { 'absent' }), $name)
    }
    if (-not (Test-Path -LiteralPath $Recipe)) { throw "Input recipe not found: $Recipe" }

    # A recording that clicks Play loads whichever save the game picks, and the two menus
    # pick different ones. Both need a save to exist, so this is checked before the run
    # rather than discovered as a mysteriously clean result.
    $savesDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Louqou\TravellersRest\GameSaves'
    $saveCount = @(Get-ChildItem -LiteralPath $savesDir -Recurse -File -Filter '*.save' -ErrorAction SilentlyContinue).Count
    if ($saveCount -lt 2) {
        Say ("  WARNING: only {0} save file(s) found. The recordings need at least 2, because" -f $saveCount) 'Yellow'
        Say '  the menu with Mod Config Menu loads the newest and the menu without it loads' 'Yellow'
        Say '  the second newest.' 'Yellow'
    } else {
        Say ("  {0} save files available" -f $saveCount)
    }

    # AutoHotkey v2 is needed only to replay a recording. The profiler itself never needs
    # it, and this script is a development tool rather than part of the mod.
    $ahkExe = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe'
    if (-not (Test-Path -LiteralPath $ahkExe)) { $ahkExe = 'C:\Program Files\AutoHotkey\v2\AutoHotkey.exe' }
    if (-not (Test-Path -LiteralPath $ahkExe)) { throw "AutoHotkey v2 not found. It is needed only for -Replay." }
    $player = Join-Path $toolsRoot 'play-input.ahk'
    if (-not (Test-Path -LiteralPath $player)) { throw "AutoHotkey player not found: $player" }

    # Wait for the game to signal that it is interactive, rather than sleeping for a fixed
    # time. The marker is written by the profiler when loading finishes, so this returns as
    # soon as the menu is actually ready and never waits longer than $MenuWait.
    $marker = Join-Path $reportDir 'ready-for-input.txt'
    $signalled = Wait-WithProgress -What 'the game to reach its main menu' -TimeoutSeconds $MenuWait -Condition {
        (Test-Path -LiteralPath $marker) -or
        (-not (Get-Process -Id $game.Id -ErrorAction SilentlyContinue))
    }
    if (-not $signalled) {
        Say '  the marker never appeared; replaying anyway, but the menu may not be ready' 'Yellow'
    }

    Say ("  replaying {0} ({1:N0}s elapsed so far)" -f (Split-Path $Recipe -Leaf), $overallTimer.Elapsed.TotalSeconds)
    & $ahkExe $player $Recipe --delay 500

    # Wait for the load the replay triggered to actually finish, so the report has it.
    $sawLoad = Wait-WithProgress -What 'the save load to finish' -TimeoutSeconds 45 -PollMs 1000 -Condition {
        $tail = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Encoding UTF8 -Tail 40 } else { @() }
        ($tail | Where-Object { $_ -match 'unload LoadingScene -> Gameplay' }) -or
        (-not (Get-Process -Id $game.Id -ErrorAction SilentlyContinue))
    }
    if (-not $sawLoad) {
        Say '  no save load was seen. The replay probably clicked nothing, so this run will' 'Yellow'
        Say '  measure an idle menu. Check that the recording matches the installed mods.' 'Yellow'
    }

    # The memory baseline lands 45 s after the game becomes interactive; give it room and
    # let some gameplay accumulate so growth is a real measurement.
    Say ("  letting the game run {0}s so the memory baseline can land ({1:N0}s elapsed so far)" -f `
        $Seconds, $overallTimer.Elapsed.TotalSeconds)
    $left = $Seconds
    while ($left -gt 0) {
        Start-Sleep -Seconds ([Math]::Min(5, $left))
        $left -= 5
        if ($left -gt 0) {
            Say ("    ... {0}s of play left ({1:N0}s total elapsed)" -f $left, $overallTimer.Elapsed.TotalSeconds) 'DarkGray'
        }
    }
    $replayed = $true
} else {
    Say ("  letting the game run {0}s ({1:N0}s elapsed so far)" -f $Seconds, $overallTimer.Elapsed.TotalSeconds)
    $left = $Seconds
    while ($left -gt 0) {
        Start-Sleep -Seconds ([Math]::Min(5, $left))
        $left -= 5
        if ($left -gt 0) { Say ("    ... {0}s left" -f $left) 'DarkGray' }
    }
}

if (-not $KeepRunning) {
    Say ''
    Say 'Stop' 'Cyan'
    # Terminated directly rather than by replaying the in-game Exit sequence. Quitting
    # through the menu costs another 10-15 s and measures the game's shutdown, which is
    # not what any of these checks are about. The game's shutdown can be measured
    # deliberately if it ever becomes interesting.
    Check 'game process killed cleanly' (Stop-Game)
}

# ---------------------------------------------------------------- analyse
Say ''
Say 'Log evidence' 'Cyan'
$logLines = if (Test-Path -LiteralPath $logPath) {
    Get-Content -LiteralPath $logPath -Encoding UTF8
} else { @() }

Say ("  {0} log lines from this run" -f $logLines.Count)

function LogHas([string]$pattern) {
    return [bool]($logLines | Where-Object { $_ -match $pattern } | Select-Object -First 1)
}

Check 'plugin reported itself loaded' (LogHas 'Performance Diagnostic 0\.1\.0 loaded')
Check 'runtime initialised' (LogHas 'Profiler runtime initialised')
Check 'loading tracker installed' (LogHas 'Loading tracker installed')
Check 'per-frame hook attached' (LogHas 'Per-frame hook attached')
Check 'heartbeat is live' (LogHas 'heartbeat is live')
Check 'no plugin Awake failure' (-not (LogHas 'Awake failed'))
Check 'no window draw failure' (-not (LogHas 'failed to draw'))
Check 'no report write failure' (-not (LogHas 'Could not write the report'))

if ($Replay) {
    Check 'a save load was observed' (LogHas 'unload LoadingScene -> Gameplay')
    # The log says either "over N measured frames" or "over N frames"; both are fine, so
    # the check is for a non-zero frame count on a load line.
    $frameLine = $logLines | Where-Object { $_ -match 'over (\d+) (?:measured )?frames' -and [int]$Matches[1] -gt 0 } | Select-Object -First 1
    Check 'frame timing was captured during a load' ([bool]$frameLine) $(if ($frameLine) { ($frameLine.Trim() -split 'over ')[-1] } else { 'no non-zero frame count' })

    # A recording that clicks the wrong thing can still reach a load window by opening a
    # save, so "a save load was observed" alone does not prove the recording is right. The
    # game's own log does: a real save load produces a burst of sprite-outline failures,
    # because the game tries to outline sprites from textures it cannot read. A run with
    # almost none of those loaded something other than a normal save.
    $playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Louqou\TravellersRest\Player.log'
    $outlineErrors = 0
    if (Test-Path -LiteralPath $playerLog) {
        $outlineErrors = @(Select-String -LiteralPath $playerLog -Pattern 'Sprite outline generation failed' -ErrorAction SilentlyContinue).Count
    }
    Check 'the game actually loaded a save (sprite-outline burst)' ($outlineErrors -ge 100) "$outlineErrors outline failures"
} else {
    Check 'a startup load was observed' (LogHas 'LoadingScene -> Inicio')
}

# ---------------------------------------------------------------- report
Say ''
Say 'Report' 'Cyan'
$reportExists = Test-Path -LiteralPath $reportPath
Check 'report was written without any key press' $reportExists
if ($reportExists) {
    $report = Get-Content -LiteralPath $reportPath -Encoding UTF8
    $reportText = $report -join "`n"

    Check 'report has a Loading section' ($reportText -match 'Loading\r?\n-------')
    Check 'report has a Memory section' ($reportText -match 'Memory\r?\n------')
    Check 'report has a Mod list section' ($reportText -match 'Mod list')
    Check 'report names at least one mod' ($reportText -match 'GUID')

    $loadMatches = [regex]::Matches($reportText, 'duration ([0-9.]+) s')
    $durations = $loadMatches | ForEach-Object { [double]$_.Groups[1].Value }
    if ($durations) {
        $max = ($durations | Measure-Object -Maximum).Maximum
        Check 'at least one load duration measured' ($max -gt 0) ("max {0:N2}s" -f $max)
    } else {
        Check 'at least one load duration measured' $false 'no durations in the report'
    }

    if ($reportText -match 'Growth since') {
        Check 'memory growth is reported' $true
    } else {
        Check 'memory growth is reported' $false 'no growth line'
    }
}

if ($ShowReport -and $reportExists) {
    Say ''
    Say '--- report ---' 'DarkGray'
    Get-Content -LiteralPath $reportPath -Encoding UTF8 | ForEach-Object { Say ("  " + $_) 'DarkGray' }
}

# ---------------------------------------------------------------- verdict
Say ''
$failed = @($results.Keys | Where-Object { -not $results[$_] })
Say ("Total run time: {0:N0}s" -f $overallTimer.Elapsed.TotalSeconds) 'DarkGray'
Say ''
if ($failed.Count -eq 0) {
    Say ("ALL {0} CHECKS PASSED" -f $results.Count) 'Green'
    exit 0
}

Say ("{0} of {1} CHECKS FAILED:" -f $failed.Count, $results.Count) 'Red'$failed | ForEach-Object { Say ("  - " + $_) 'Red' }

if (Test-Path -LiteralPath $reportPath) {
    Say ''
    Say 'Report was:' 'DarkGray'
    Get-Content -LiteralPath $reportPath -Encoding UTF8 | ForEach-Object { Say ("  " + $_) 'DarkGray' }
}

exit 1
