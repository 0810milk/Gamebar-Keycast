@echo off
rem Pad display preview window (no Game Bar needed). Python 3 stdlib only.
setlocal
set "PYW=%LOCALAPPDATA%\Programs\Python\Python312\pythonw.exe"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if exist "%PYW%" goto usepyw
if exist "%PY%" goto usepy
start "" pythonw "%~dp0pad_preview.py" %*
goto end
:usepyw
start "" "%PYW%" "%~dp0pad_preview.py" %*
goto end
:usepy
start "" "%PY%" "%~dp0pad_preview.py" %*
:end
endlocal
