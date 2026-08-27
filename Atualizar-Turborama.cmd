@echo off
setlocal
title Compilar Turborama
cd /d "%~dp0"
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 nao foi encontrado. Instale o PowerShell 7 para compilar o Turborama.
  pause
  exit /b 1
)
echo.
echo O processo inseguro antigo foi removido: ele incorporava chaves e links no EXE.
echo.
echo Candidato assinado (ainda sujeito ao checklist de producao):
echo   Consulte README.md: o candidato assinado exige pins independentes de certificado, timestamp, tag GPG, Git/GPG e arvore completa do SDK .NET.
echo.
echo Staging local, explicitamente nao publicavel:
echo   pwsh -File .\tools\Build-Production.ps1 -UnsignedStaging -AllowDirty
echo.
echo Consulte README.md e SECURITY.md antes de distribuir qualquer pacote.
pause
exit /b 2
