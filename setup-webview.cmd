@echo off
REM Installs the desktop window backend (pywebview) into a local .venv.
REM Only needed once. Safe to re-run.
setlocal
cd /d "%~dp0"

if exist ".venv\Scripts\python.exe" (
  echo Using existing .venv
) else (
  echo Creating .venv ...
  where py >nul 2>nul
  if %errorlevel%==0 (
    py -3.12 -m venv .venv
  ) else (
    python -m venv .venv
  )
  if errorlevel 1 goto :failed
)

echo Installing pywebview (window backend) ...
".venv\Scripts\python.exe" -m pip install --quiet --upgrade pip
".venv\Scripts\python.exe" -m pip install --quiet pywebview
if errorlevel 1 goto :failed

echo.
echo Done. Start the window with:
echo    start-app.cmd
echo.
pause
exit /b 0

:failed
echo.
echo Install failed. The app still works in a browser:
echo    py app.py --browser
pause
exit /b 1
