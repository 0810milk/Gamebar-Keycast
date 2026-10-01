@echo off
rem P1 test: start the native input receiver on a TEST pipe, then open the state checker.
rem Nothing here touches the current widget/companion setup (different pipe name).
setlocal
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
set "PIPE=KeyDisplayInputTest"
start "" "%~dp0KeyDisplayInput.exe" --pipe=%PIPE% --hz=240
timeout /t 2 /nobreak >nul
if exist "%PY%" (
  "%PY%" "%~dp0..\tools\input-state-check.py" --pipe=%PIPE% --hz=5
) else (
  python "%~dp0..\tools\input-state-check.py" --pipe=%PIPE% --hz=5
)
echo.
echo Checker closed. The receiver is still running (test pipe: %PIPE%).
echo To stop it: taskkill /IM KeyDisplayInput.exe /F
pause
endlocal
