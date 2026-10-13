@echo off
rem Copyright (c) 2026 dev-willbird1936
setlocal
title DCU Multi-Agent Regression
pushd "%~dp0"
dotnet build "src\ShadowUse\ShadowUse.csproj" -c Release
if errorlevel 1 goto :failed
dotnet build "tests\WindowStressFixture\WindowStressFixture.csproj" -c Release
if errorlevel 1 goto :failed
where py >nul 2>nul
if not errorlevel 1 (
  py -3 tests\multi_agent_regression.py
  if errorlevel 1 goto :failed
  popd
  exit /b 0
)
python tests\multi_agent_regression.py
if errorlevel 1 goto :failed
popd
exit /b 0
:failed
popd
exit /b 1
