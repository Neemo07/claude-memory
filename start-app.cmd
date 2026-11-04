@echo off
REM claude-memory desktop window. Double-click this.
REM
REM Launches with pythonw.exe so there is NO console window at all: the window
REM opens and this script exits immediately. Errors are not lost - the app shows
REM a native dialog and writes claude-memory-error.log.
REM
REM For a console (troubleshooting):  py app.py --debug

setlocal
cd /d "%~dp0"
set "HERE=%CD%"

set "PY="
if exist "%HERE%\.venv\Scripts\pythonw.exe" set "PY=%HERE%\.venv\Scripts\pythonw.exe"
if not defined PY (
  for %%P in (pythonw.exe) do if not "%%~$PATH:P"=="" set "PY=%%~$PATH:P"
)
if not defined PY (
  for %%P in (py.exe) do if not "%%~$PATH:P"=="" (
    py -3.12 -c "import sys,os;sys.exit(0 if os.path.exists(os.path.join(sys.base_prefix,'pythonw.exe')) else 1)" >nul 2>nul
    if not errorlevel 1 (
      start "" py -3.12 -c "import runpy,sys;sys.argv=['app.py']+sys.argv[1:];runpy.run_path(r'%HERE%\app.py',run_name='__main__')" %*
      exit /b 0
    )
  )
)

if not defined PY (
  echo.
  echo pythonw.exe was not found, so the app cannot start without a console.
  echo Install Python 3.9 or newer from https://python.org and tick
  echo "Add python.exe to PATH", then run this file again.
  echo.
  pause
  exit /b 1
)

start "claude-memory" "%PY%" "%HERE%\app.py" %*
exit /b 0
