# Answers one question: does the recorded save-load input still work when Mod Config Menu
# is not installed?
#
# Why it matters: Mod Config Menu adds a Mods entry to the main menu, which shifts every
# other button down. The recording was made with it present. If the recorded clicks land on
# the wrong control without it, the recording is only valid on a machine that has that mod —
# and a replay that misses silently produces a run that looks successful.
#
# It disables Mod Config Menu by renaming its DLL, runs the replay, checks the game's own
# log for evidence that a save actually loaded, then restores the DLL. Nothing is left
# changed, including on Ctrl+C.

[CmdletBinding()]
param(
    # Where Travellers Rest is installed.
    [string]$GameRoot = '',

    # Recording to replay.
    [string]$Recipe = '',

    # Seconds to wait after launching before replaying.
    [int]$MenuWait = 25,

    # Seconds to wait after the replay for the load to finish.
    [int]$AfterReplay = 45,

    # Leave Mod Config Menu disabled afterwards, instead of restoring it.
    [switch]$LeaveDisabled
)

$ErrorActionPreference = 'Stop'

$scriptsRoot = Split-Path -Parent $PSCommandPath
$modRoot     = Split-Path -Parent $scriptsRoot
$toolsRoot   = Join-Path $modRoot 'tools\load-automation'

if (-not $GameRoot) { $GameRoot = $env:TRAVELLERS_REST_DIR }
if (-not $GameRoot) {
    $GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Travellers Rest\Windows'
}
if (-not $Recipe) { $Recipe = Join-Path $toolsRoot 'load-save-modded.txt' }

$exePath   = Join-Path $GameRoot 'TravellersRest.exe'
$player    = Join-Path $toolsRoot 'play-input.ahk'
$ahkExe    = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe'
if (-not (Test-Path -LiteralPath $ahkExe)) { $ahkExe = 'C:\Program Files\AutoHotkey\v2\AutoHotkey.exe' }
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Louqou\TravellersRest\Player.log'

foreach ($r in @(
    @{ P = $exePath; W = 'TravellersRest.exe (pass -GameRoot or set TRAVELLERS_REST_DIR)' },
    @{ P = $Recipe;  W = 'input recording' },
    @{ P = $ahkExe;  W = 'AutoHotkey v2' },
    @{ P = $player;  W = 'play-input.ahk' }
)) {
    if (-not (Test-Path -LiteralPath $r.P)) { throw ("Not found: {0} — {1}" -f $r.P, $r.W) }
}

function Say([string]$m, [string]$c = 'Gray') { Write-Host $m -ForegroundColor $c }

function Stop-Game {
    $p = Get-Process -Name 'TravellersRest' -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force; Start-Sleep -Seconds 3 }
}

# Mod Config Menu may sit in its own folder or directly in plugins, and may be present under
# any of the names people use to disable a plugin.
function Find-Mcm {
    $folders = @(
        (Join-Path $GameRoot 'BepInEx\plugins\Mod Config Menu'),
        (Join-Path $GameRoot 'BepInEx\plugins')
    )
    $names = @('ModConfigMenu.dll', 'ModConfigMenu._dll', 'ModConfigMenu.dll.disabled')
    foreach ($folder in $folders) {
        foreach ($name in $names) {
            $candidate = Join-Path $folder $name
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    return $null
}

$mcm = Find-Mcm
if (-not $mcm) {
    Say 'Mod Config Menu was not found under any known name.' 'Yellow'
    Say 'If it is already renamed to something else, restore it and run this again.' 'Yellow'
    exit 1
}

# If it is already disabled, enable it first so the test compares like with like.
$wasDisabled = $mcm -notlike '*.dll'
if ($wasDisabled) {
    Say ("  Mod Config Menu was already disabled as '{0}'; enabling it first so the test is comparable." -f (Split-Path $mcm -Leaf)) 'Yellow'
    Move-Item -LiteralPath $mcm -Destination (Join-Path (Split-Path $mcm -Parent) 'ModConfigMenu.dll') -Force
    $mcm = Join-Path (Split-Path $mcm -Parent) 'ModConfigMenu.dll'
}

$mcmDisabled = "$mcm.disabled-by-test"

function Disable-Mcm {
    if (Test-Path -LiteralPath $mcm) { Move-Item -LiteralPath $mcm -Destination $mcmDisabled -Force }
    return -not (Test-Path -LiteralPath $mcm)
}

function Enable-Mcm {
    if (Test-Path -LiteralPath $mcmDisabled) { Move-Item -LiteralPath $mcmDisabled -Destination $mcm -Force }
    return Test-Path -LiteralPath $mcm
}

trap {
    Say ''
    Say 'Interrupted; restoring Mod Config Menu.' 'Yellow'
    Enable-Mcm | Out-Null
    Stop-Game
    break
}

Say ''
Say '=== Does the recording still work without Mod Config Menu? ===' 'Cyan'
Say ("  MCM      : {0}" -f $mcm)
Say ("  recipe   : {0}" -f (Split-Path $Recipe -Leaf))

Stop-Game
if (-not (Disable-Mcm)) { throw 'Could not disable Mod Config Menu; aborting.' }
Say '  Mod Config Menu disabled' 'Yellow'

Remove-Item -LiteralPath $playerLog -ErrorAction SilentlyContinue

$game = Start-Process -FilePath $exePath -WorkingDirectory $GameRoot -PassThru
Say ("  game started, pid {0}. Waiting {1}s for the menu" -f $game.Id, $MenuWait)
Start-Sleep -Seconds $MenuWait

Say '  replaying the recording'
& $ahkExe $player $Recipe --delay 500

Say ("  waiting {0}s for the load" -f $AfterReplay)
Start-Sleep -Seconds $AfterReplay

$loaded = $false
$outlineErrors = 0
$logLines = 0
if (Test-Path -LiteralPath $playerLog) {
    $lines = Get-Content -LiteralPath $playerLog -Encoding UTF8
    $logLines = $lines.Count
    $loaded = [bool]($lines | Where-Object { $_ -match 'StartEnteringGame' })
    $outlineErrors = ($lines | Where-Object { $_ -match 'Sprite outline generation failed' }).Count
}

Stop-Game
if (-not $LeaveDisabled) {
    Enable-Mcm | Out-Null
    Say '  Mod Config Menu restored' 'Green'
} else {
    Say '  Mod Config Menu left disabled, as requested' 'Yellow'
}

Say ''
Say '=== Result ===' 'Cyan'
Say ("  player.log lines      : {0}" -f $logLines)
Say ("  loaded a save         : {0}" -f $loaded) -ForegroundColor $(if ($loaded) { 'Green' } else { 'Red' })
Say ("  sprite outline errors : {0}" -f $outlineErrors)
Say ''
if ($loaded) {
    Say '  The recording still loads a save without Mod Config Menu.' 'Green'
    Say '  So the recorded coordinates are not tied to that mod, and the recording is' 'Green'
    Say '  portable to an install that does not have it.' 'Green'
} else {
    Say '  The recording did NOT load a save without Mod Config Menu.' 'Red'
    Say '  That means its coordinates depend on the Mods button being present, so a' 'Red'
    Say '  separate recording is needed for an install without that mod.' 'Red'
    Say '  Re-record with tools\load-automation\record-input.ahk and pass it with -Recipe.' 'Yellow'
}
