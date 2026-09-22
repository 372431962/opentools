' ---------------------------------------------------------------
' DesktopFish - rescue script: make sure desktop icons are visible
' again (silently). Use it if the icons stayed hidden after a crash.
' Checks the exit code and reports failure honestly.
' ---------------------------------------------------------------
Option Explicit

Dim fso, shell, q, baseDir
Dim rc, exePath, dotnet

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
q = Chr(34)
baseDir = fso.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = baseDir

Function ExpandEnv(s)
    ExpandEnv = shell.ExpandEnvironmentStrings(s)
End Function

Function FirstIn(rootFolder, prefix, exeName)
    Dim d
    FirstIn = ""
    If Not fso.FolderExists(rootFolder) Then Exit Function
    On Error Resume Next
    For Each d In fso.GetFolder(rootFolder).SubFolders
        If LCase(Left(d.Name, Len(prefix))) = LCase(prefix) Then
            If fso.FileExists(d.Path & "\" & exeName) Then
                FirstIn = d.Path & "\" & exeName
                Exit Function
            End If
        End If
    Next
    On Error GoTo 0
End Function

Function Works(cmd)
    Dim r
    Works = False
    On Error Resume Next
    r = shell.Run("cmd /c " & q & q & cmd & q & " --version", 0, True)
    If Err.Number = 0 And r = 0 Then Works = True
    On Error GoTo 0
End Function

Function FindDotnet()
    Dim cand
    FindDotnet = ""
    If Works("dotnet") Then FindDotnet = "dotnet" : Exit Function
    For Each cand In Array( _
        FirstIn(ExpandEnv("%ProgramFiles%"), "dotnet", "dotnet.exe"), _
        FirstIn(ExpandEnv("%ProgramFiles(x86)%"), "dotnet", "dotnet.exe"), _
        ExpandEnv("%LocalAppData%\Microsoft\dotnet\dotnet.exe"), _
        ExpandEnv("%LocalAppData%\dotnet\dotnet.exe"))
        If Len(cand) > 0 Then
            If fso.FileExists(cand) Then FindDotnet = cand : Exit Function
        End If
    Next
End Function

exePath = baseDir & "\dist\DesktopFish.exe"
rc = 1

If fso.FileExists(exePath) Then
    On Error Resume Next
    rc = shell.Run(q & exePath & q & " --restore", 0, True)
    If Err.Number <> 0 Then rc = 1
    On Error GoTo 0
Else
    If Not fso.FileExists(baseDir & "\DesktopFish.csproj") Then
        MsgBox "No dist\DesktopFish.exe and no DesktopFish.csproj next to this script.", 16, "DesktopFish"
        WScript.Quit 1
    End If

    dotnet = FindDotnet()
    If dotnet = "" Then
        MsgBox "dotnet not found, cannot run the rescue script." & vbCrLf & _
               "Alternative: restart explorer.exe.", 16, "DesktopFish"
        WScript.Quit 1
    End If

    Dim restoreCmd
    restoreCmd = "cmd /c " & q & q & dotnet & q & " run --project " & q & baseDir & "\DesktopFish.csproj" & q & " --restore"
    On Error Resume Next
    rc = shell.Run(restoreCmd, 0, True)
    If Err.Number <> 0 Then rc = 1
    On Error GoTo 0
End If

If rc = 0 Then
    MsgBox "Done: desktop icons should be visible again.", 64, "DesktopFish"
Else
    MsgBox "Rescue script failed (exit code " & rc & ")." & vbCrLf & _
           "Try restarting explorer.exe, or run: run.bat --restore", 16, "DesktopFish"
    WScript.Quit 1
End If
