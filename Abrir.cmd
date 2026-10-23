@echo off
setlocal
set "AHK_MANAGER_NATIVE=%~dp0Presentation.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Bezoro.AhkManager.WinUI.exe"
if exist "%AHK_MANAGER_NATIVE%" (
  start "" "%AHK_MANAGER_NATIVE%"
  exit /b 0
)
echo O aplicativo nativo ainda nao foi compilado neste repositorio.
echo Execute tools\Build-WinUI.ps1 ou abra Bezoro.AhkManager.WinUI.exe na pasta completa da distribuicao extraida.
exit /b 2
