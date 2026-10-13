@echo off
rem Copyright (c) 2026 dev-willbird1936
setlocal
title DCU Release Preparation
where pwsh >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 is required. Install it or run scripts\prepare-release.ps1 with PowerShell 7.
  exit /b 1
)
pwsh -NoProfile -File "%~dp0scripts\prepare-release.ps1" %*
exit /b %errorlevel%
