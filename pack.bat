@echo off
setlocal enableextensions

REM ====== Config ======
set "VENV_DIR=venv"
set "MAIN=audio_recorder.py"
set "APP_NAME=AudioRecorder"
set "ICON=icons\app.ico"      REM optional; if missing, the build continues without it
set "DIST_DIR=dist"
set "BUILD_DIR=build"
set "SPECPATH=."

REM ====== Checks ======
where python >nul 2>nul || goto :python_error
if not exist "%MAIN%" goto :main_missing

REM ====== Ensure venv ======
if not exist "%VENV_DIR%\Scripts\python.exe" (
  echo --- Creating virtual environment...
  python -m venv "%VENV_DIR%" || goto :venv_error
)

echo --- Activating virtual environment...
call "%VENV_DIR%\Scripts\activate.bat" || goto :venv_error

REM ====== Upgrade base tooling ======
echo --- Upgrading pip/setuptools/wheel...
python -m pip install --upgrade pip setuptools wheel || goto :install_error

REM ====== Install runtime deps + PyInstaller ======
echo --- Installing requirements...
pip install --upgrade --upgrade-strategy eager -r requirements.txt || goto :install_error

echo --- Installing PyInstaller...
pip install "pyinstaller>=6.0" || goto :install_error

REM ====== Clean old build artifacts ======
if exist "%BUILD_DIR%" (
  echo --- Cleaning build dir...
  rmdir /s /q "%BUILD_DIR%"
)
if exist "%DIST_DIR%" (
  echo --- Cleaning dist dir...
  rmdir /s /q "%DIST_DIR%"
)

REM ====== Build command ======
set "ICON_FLAG="
if exist "%ICON%" set "ICON_FLAG=--icon=%ICON%"

REM Ensure sv_ttk theme assets get included
set "COLLECT_FLAGS=--collect-all sv_ttk --collect-data sv_ttk --collect-submodules sv_ttk"

echo --- Running PyInstaller...
pyinstaller ^
  --noconfirm ^
  --clean ^
  --onefile ^
  --windowed ^
  --name "%APP_NAME%" ^
  %ICON_FLAG% ^
  %COLLECT_FLAGS% ^
  --specpath "%SPECPATH%" ^
  "%MAIN%" || goto :build_error

echo(
echo ============================================================
echo   Build complete!
echo   EXE: %DIST_DIR%\%APP_NAME%.exe
echo ============================================================
echo(
pause
goto :eof


:python_error
echo(
echo ERROR: Python not found on PATH.
echo(
pause
exit /b 1

:main_missing
echo(
echo ERROR: Could not find "%MAIN%" in the current directory.
echo(
pause
exit /b 1

:venv_error
echo(
echo ERROR: Failed to create/activate the virtual environment.
echo(
pause
exit /b 1

:install_error
echo(
echo --------------------------------------------------------------
echo ERROR: Failed to install required Python packages or tools.
echo --------------------------------------------------------------
echo(
pause
exit /b 1

:build_error
echo(
echo --------------------------------------------------------------
echo ERROR: PyInstaller build failed.
echo --------------------------------------------------------------
echo(
pause
exit /b 1
