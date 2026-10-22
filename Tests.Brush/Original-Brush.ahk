#Requires AutoHotkey v2.0
#SingleInstance Force
#NoTrayIcon
#Warn

; =============================================================================
; Volume to Brush Size - Remaps volume keys to brush size in painting apps
; F13/F14 become [ ] in painting apps, Volume Down/Up elsewhere
; =============================================================================

; =============================================================================
; Initialization
; =============================================================================

if !A_IsAdmin {
    try Run('*RunAs "' A_AhkPath '" "' A_ScriptFullPath '"')
    ExitApp
}

; =============================================================================
; Configuration
; =============================================================================

global Config := {
    BrushSizeSmaller: "[",
    BrushSizeLarger: "]"
}

; Painting/sculpting applications where volume keys become brush size controls
global PaintingApps := Map(
    "blender.exe",          1,
    "zbrush.exe",           1,
    "clipstudiopaint.exe",  1,
    "leonardo.exe",         1,
    "3dcoat.exe",           1,
    "substancepainter.exe", 1,
    "corelpainter.exe",     1
)

; =============================================================================
; Core Functions
; =============================================================================

IsPaintingApp() {
    try {
        hwnd := WinActive("A")
        if (!hwnd)
            return false
        exe := WinGetProcessName(hwnd)
        return PaintingApps.Has(StrLower(exe))
    }
    return false
}

; =============================================================================
; Hotkeys - Context Sensitive
; =============================================================================

#HotIf IsPaintingApp()
F13:: SendInput("{Blind}" Config.BrushSizeSmaller)
F14:: SendInput("{Blind}" Config.BrushSizeLarger)
#HotIf

; =============================================================================
; Hotkeys - Default (Volume Control)
; =============================================================================

F13:: SendInput("{Volume_Down}")
F14:: SendInput("{Volume_Up}")
