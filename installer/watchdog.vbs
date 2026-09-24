' KeyDisplay companion watchdog (0.9.3)
' Called by a scheduled task every 5 minutes: starts the companion process
' if it is not running. Kept pure-ASCII on purpose: wscript reads .vbs as
' ANSI, so non-ASCII comments could break on some locales.
'
' Why this exists: the Game Bar widget cannot reliably start a desktop
' process from its sandbox, and the Run autostart entry can be disabled by
' the system or the user. A scheduled task is hosted by the Task Scheduler
' service, so it keeps working, and this watchdog also self-heals the case
' where the companion gets killed.
'
' WScript runs windowless; process enumeration every 5 minutes is negligible.
Option Explicit

Dim exePath, svc, procs, sh
exePath = "C:\Program Files\KeyDisplay\KeyDisplayCompanion.exe"

On Error Resume Next
Set svc = GetObject("winmgmts:\\.\root\cimv2")
If Err.Number <> 0 Then WScript.Quit 0
On Error GoTo 0

On Error Resume Next
Set procs = svc.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='KeyDisplayCompanion.exe'")
If Err.Number <> 0 Then WScript.Quit 0
On Error GoTo 0

If procs.Count = 0 Then
    Set sh = CreateObject("WScript.Shell")
    ' 0 = hidden window, False = do not wait
    sh.Run """" & exePath & """", 0, False
End If
