#Requires AutoHotkey v2.0
; Replays an input sequence recorded by record-input.ahk.
;
; This is a development tool, not part of the mod.
;
; Usage:
;   play-input.ahk [input-file] [--speed <factor>] [--delay <ms>] [--window <title>]
;
;   --speed 2        play twice as fast
;   --delay 500      add a fixed pause after every event, which covers a cold start being
;                    slower than it was during recording
;   --window <title> window to activate first; defaults to the game's executable
;
; With no input file it replays recorded-input.txt next to this script.

#SingleInstance Force
SendMode("Event")
SetKeyDelay(40, 40)
CoordMode("Mouse", "Screen")

Init() {
    global inputFile, speed, extraDelay, targetWindow, scriptDir, logFile
    SplitPath(A_ScriptFullPath, , &scriptDir)
    inputFile := scriptDir "\recorded-input.txt"
    logFile := scriptDir "\play-input.log"
    speed := 1.0
    extraDelay := 0
    targetWindow := "ahk_exe TravellersRest.exe"
}

Init()

index := 1
while (index <= A_Args.Length) {
    arg := A_Args[index]
    if (arg = "--speed" && index < A_Args.Length) {
        speed := A_Args[index + 1] + 0.0
        index += 2
    } else if (arg = "--delay" && index < A_Args.Length) {
        extraDelay := A_Args[index + 1] + 0
        index += 2
    } else if (arg = "--window" && index < A_Args.Length) {
        targetWindow := A_Args[index + 1]
        index += 2
    } else if (arg != "") {
        inputFile := arg
        index += 1
    } else {
        index += 1
    }
}

Log(text) {
    global logFile
    FileAppend(text "`n", logFile)
}

if (!FileExist(inputFile)) {
    Log("input file not found: " inputFile)
    ExitApp(1)
}

; Bring the target forward first, because a replay sent to whatever happens to be in front
; is worse than useless.
if (WinExist(targetWindow)) {
    WinActivate(targetWindow)
    WinWaitActive(targetWindow, , 10)
    Sleep(400)
    Log("activated " targetWindow)
} else {
    Log("WARNING: window '" targetWindow "' not found; sending input to the active window")
}

Log("playing " inputFile " at speed " speed " with extra delay " extraDelay "ms")

count := 0
for line in StrSplit(FileRead(inputFile, "UTF-8"), "`n", "`r") {
    line := Trim(line)
    if (line = "" || SubStr(line, 1, 1) = "#")
        continue

    parts := StrSplit(line, " ", , 2)
    verb := parts[1]
    rest := parts.Length >= 2 ? parts[2] : ""

    switch verb {
        case "wait":
            delay := (rest + 0) / speed + extraDelay
            if (delay > 0)
                Sleep(Round(delay))

        case "move":
            coords := StrSplit(rest, ",")
            MouseMove(coords[1] + 0, coords[2] + 0, 0)

        case "click":
            if (rest = "MouseRight")
                Click("Right")
            else
                Click("Left")
            count++

        case "key":
            Send("{" rest "}")
            count++

        case "text":
            SendText(rest)
            count++
    }
}

Log("replayed " count " input events")
ExitApp(0)
