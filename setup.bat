@echo off
rem Copyright (c) 2026 dev-willbird1936
setlocal
title DCU Setup

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK is required.
  exit /b 1
)

dotnet restore "%~dp0src\ShadowUse\ShadowUse.csproj"
if errorlevel 1 exit /b 1

dotnet restore "%~dp0tests\ShadowUse.Tests\ShadowUse.Tests.csproj"
if errorlevel 1 exit /b 1

dotnet build "%~dp0src\ShadowUse\ShadowUse.csproj" -c Release --no-restore
exit /b %errorlevel%
