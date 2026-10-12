#Requires AutoHotkey v2.0
; Records mouse clicks and key presses, with timing, into a replayable file.
;
; This is a development tool, not part of the mod. The game's menus cannot be driven
; reliably by guessing coordinates, because Mod Config Menu shifts them and Windows display
; scaling adds another coordinate space on top. Recording the real sequence once gives an
; exact, repeatable script.
;
; Usage:
;   record-input.ahk [output-file] [--clicks-only] [--keys-only]
;
; Press F10 to start and stop recording. Press F12 to quit.
;
; Output format, one event per line:
;   wait <ms>          elapsed time before the next event
;   move <x>,<y>       cursor position
;   click <button>     MouseLeft or MouseRight, at the last moved position
;   key <name>         a key such as Escape, Space, Enter, F5
;
; Two AutoHotkey v2 traps are worth knowing about, because both cost real time here:
;
;   * FileAppend REQUIRES a filename. There is no default target, and omitting it is a
;     runtime error rather than a style problem.
;   * The file must have CRLF line endings. With LF-only endings this script starts, shows
;     its tray icon, and then silently does nothing at all — no error, no output.

#SingleInstance Force
SendMode("Event")

global outputFile := ""
global recordClicks := true
global recordKeys := true
global recording := false
global lastTick := 0
global eventCount := 0
global scriptDir := ""
global logFile := ""

SplitPath(A_ScriptFullPath, , &scriptDir)
logFile := scriptDir "\record-input.log"

; Read the command line first, so an explicit path wins over the default. A relative path
; is resolved against THIS SCRIPT's folder, not the shell's working directory: running the
; recorder from the game folder with ".\tools\load-automation\x.txt" otherwise looked for
; that folder next to the game and failed with "the system cannot find the path specified".
outputFile := scriptDir "\recorded-input.txt"
for arg in A_Args {
    if (arg = "--clicks-only")
        recordKeys := false
    else if (arg = "--keys-only")
        recordClicks := false
    else if (arg != "") {
        ; Strip a leading ".\" or "./", which people write out of habit and which would
        ; otherwise become a literal folder name when joined to the script folder.
        if (SubStr(arg, 1, 2) = ".\" || SubStr(arg, 1, 2) = "./")
            arg := SubStr(arg, 3)
        isAbsolute := (SubStr(arg, 1, 1) = "\") || (StrLen(arg) > 1 && SubStr(arg, 2, 1) = ":")
        outputFile := isAbsolute ? arg : scriptDir "\" arg
    }
}

; Validate and prepare the output path now, rather than failing when F10 is pressed. A
; "system cannot find the path specified" error from FileAppend means the folder was
; missing, so the folder is created here and the resolved path is logged.
;
; AutoHotkey's SplitPath does NOT behave as its parameter names suggest. Measured on
; 2.0.26 with "C:\dir\name.txt":
;
;     SplitPath(path, , &dir, &name)          -> dir="C:\dir",  name="txt"
;     SplitPath(path, , &dir, &name, &ext)    -> name="txt",    ext="name"
;
; The fourth parameter receives only the extension and the fifth receives the name WITHOUT
; its extension — the opposite of the obvious reading. Building a path from the fourth
; produced "...\txt", a folder that does not exist, which is exactly the failure this code
; is here to prevent. So the folder is resolved with SplitPath and the filename is left
; exactly as the caller wrote it.
SplitPath(outputFile, , &outDir)
if (outDir = "")
    outDir := scriptDir
if (!DirExist(outDir)) {
    try {
        DirCreate(outDir)
    } catch as e {
        FileAppend("could not create folder [" outDir "]: " e.Message "`n", logFile)
        MsgBox("Could not create the output folder:`n" outDir "`n`n" e.Message, "Recorder", "IconX")
        ExitApp(1)
    }
}
FileAppend("output file is [" outputFile "], folder [" outDir "]`n", logFile)

Hotkey("F10", (*) => Toggle())
Hotkey("F12", (*) => ExitApp(0))

Toggle() {
    global recording, lastTick, eventCount, outputFile, logFile
    if (!recording) {
        try FileDelete(outputFile)
        try {
            FileAppend("# recorded input; replay with play-input.ahk`n", outputFile)
        } catch as e {
            FileAppend("could not write [" outputFile "]: " e.Message "`n", logFile)
            MsgBox("Could not write to:`n" outputFile "`n`n" e.Message, "Recorder", "IconX")
            return
        }
        recording := true
        lastTick := A_TickCount
        eventCount := 0
        TrayTip("Recording input", "F10 stops. Writing to " outputFile, 1)
        SoundBeep(880, 80)
    } else {
        recording := false
        try FileAppend("# " eventCount " events`n", outputFile)
        TrayTip("Recording stopped", eventCount " events written", 1)
        SoundBeep(440, 120)
    }
}

Emit(line) {
    global recording, lastTick, eventCount, outputFile
    if (!recording)
        return
    elapsed := A_TickCount - lastTick
    lastTick := A_TickCount
    try {
        FileAppend("wait " elapsed "`n", outputFile)
        if (line != "")
            FileAppend(line "`n", outputFile)
    } catch {
        return
    }
    eventCount++
}

EmitMouse(button) {
    MouseGetPos(&x, &y)
    Emit("move " x "," y)
    Emit("click " button)
}

EmitKey(name) {
    Emit("key " name)
}

; ---------------------------------------------------------------- mouse
#HotIf recording && recordClicks
~LButton::EmitMouse("MouseLeft")
~RButton::EmitMouse("MouseRight")
#HotIf

; ---------------------------------------------------------------- keys
; A whitelist rather than a wildcard, because a wildcard recorder would also capture
; whatever else is typed while it runs.
#HotIf recording && recordKeys
~Escape::EmitKey("Escape")
~Space::EmitKey("Space")
~Enter::EmitKey("Enter")
~Tab::EmitKey("Tab")
~Up::EmitKey("Up")
~Down::EmitKey("Down")
~Left::EmitKey("Left")
~Right::EmitKey("Right")
~F5::EmitKey("F5")
~F9::EmitKey("F9")
~F11::EmitKey("F11")
~Backspace::EmitKey("Backspace")
~Delete::EmitKey("Delete")
#HotIf
