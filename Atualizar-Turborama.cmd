@echo off
setlocal
title Atualizar Turborama
cd /d "%~dp0"
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 nao foi encontrado. Instale o PowerShell 7 para atualizar o Turborama.
  pause
  exit /b 1
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Atualizar-Turborama.ps1"
if errorlevel 1 (
  echo.
  echo A atualizacao falhou. Leia a mensagem acima.
  pause
  exit /b 1
)
echo.
echo Atualizacao concluida. O novo EXE esta em dist-private.
pause
