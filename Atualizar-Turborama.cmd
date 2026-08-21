@echo off
setlocal
title Atualizar Turborama
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Atualizar-Turborama.ps1"
if errorlevel 1 (
  echo.
  echo A atualizacao falhou. Leia a mensagem acima.
  pause
  exit /b 1
)
echo.
echo Atualizacao concluida. O novo EXE esta em dist-private.
pause
