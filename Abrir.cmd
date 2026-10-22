@echo off
if not exist "%~dp0publish\Bezoro.AhkManager.exe" (
  echo Executavel ausente. Consulte README.md para compilar o aplicativo.
  pause
  exit /b 1
)
start "" "%~dp0publish\Bezoro.AhkManager.exe"
