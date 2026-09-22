' ---------------------------------------------------------------
' DesktopFish - silent launcher (no console window at all).
' Double-click me to start; press ESC to stop and restore icons.
' Prefers a prebuilt dist\DesktopFish.exe; otherwise builds via dotnet.
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

' Enumerate subfolders whose name starts with prefix. VBScript has no Like operator.
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

' True if `cmd --version` succeeds.
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

If Not fso.FileExists(exePath) Then
    If Not fso.FileExists(baseDir & "\DesktopFish.csproj") Then
        MsgBox "DesktopFish.csproj not found next to this script.", 16, "DesktopFish"
        WScript.Quit 1
    End If

    dotnet = FindDotnet()
    If dotnet = "" Then
        MsgBox "dotnet SDK not found, and no prebuilt dist\DesktopFish.exe." & vbCrLf & _
               "Install .NET 8.0 or later:" & vbCrLf & _
               "https://dotnet.microsoft.com/download", 16, "DesktopFish"
        WScript.Quit 1
    End If

    Dim buildCmd
    buildCmd = "cmd /c " & q & q & dotnet & q & " publish DesktopFish.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true -o dist 2>&1"
    On Error Resume Next
    rc = shell.Run(buildCmd, 0, True)
    If Err.Number <> 0 Then rc = 1
    On Error GoTo 0
    If rc <> 0 Or Not fso.FileExists(exePath) Then
        MsgBox "Build failed. Please run publish.bat to see details.", 16, "DesktopFish"
        WScript.Quit 1
    End If
End If

shell.Run q & exePath & q, 0, False
