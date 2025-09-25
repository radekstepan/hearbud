@echo off
setlocal enableextensions

set "VENV_DIR=venv"
set "MAIN=audio_recorder.py"

where python >nul 2>nul || goto :python_error

if exist "%VENV_DIR%\Scripts\python.exe" goto :venv_ready
echo --- Creating virtual environment...
python -m venv "%VENV_DIR%" || goto :venv_error

:venv_ready
echo --- Activating virtual environment...
call "%VENV_DIR%\Scripts\activate.bat" || goto :venv_error

echo --- Upgrading pip/setuptools/wheel...
python -m pip install --upgrade pip setuptools wheel || goto :install_error

echo --- Installing requirements...
pip install --upgrade --upgrade-strategy eager -r requirements.txt || goto :install_error

echo --- Starting the Audio Recorder...
python "%MAIN%"
echo --- Application finished.
pause
goto :eof

:python_error
echo(
echo ERROR: Python not found on PATH.
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
echo --------------------------------------------------------------------
echo ERROR: Failed to install required Python packages.
echo --------------------------------------------------------------------
echo(
pause
exit /b 1
