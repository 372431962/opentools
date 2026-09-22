' ---------------------------------------------------------------
' DesktopFish - create a Desktop shortcut (Chinese name, built at runtime)
' Double-click me once; a shortcut appears on your Desktop.
' ---------------------------------------------------------------
Option Explicit

Dim fso, shell, base, desktop, lnk, name
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

base = fso.GetParentFolderName(WScript.ScriptFullName)
desktop = shell.SpecialFolders("Desktop")

' build the Chinese name at runtime to keep this file ASCII-only:
' U+684C U+9762 U+5C0F U+9C7C  =  "desktop fish"
name = ChrW(&H684C) & ChrW(&H9762) & ChrW(&H5C0F) & ChrW(&H9C7C)

Set lnk = shell.CreateShortcut(desktop & "\" & name & ".lnk")
lnk.TargetPath = base & "\start_fish.bat"
lnk.WorkingDirectory = base
lnk.WindowStyle = 7
lnk.IconLocation = "shell32.dll,44"
lnk.Description = "DesktopFish - let desktop icons swim"
lnk.Save

MsgBox "Shortcut created on Desktop: " & name, 64, "DesktopFish"
