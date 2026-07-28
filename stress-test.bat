@echo off
rem Copyright (c) 2026 dev-willbird1936
setlocal
title DCU Multi-Window Stress Test
pushd "%~dp0"

dotnet build "src\ShadowUse\ShadowUse.csproj" -c Release
if errorlevel 1 goto :failed

dotnet build "tests\WindowStressFixture\WindowStressFixture.csproj" -c Release
if errorlevel 1 goto :failed

where py >nul 2>nul
if not errorlevel 1 (
  py -3 tests\multi_window_stress_regression.py
  if errorlevel 1 goto :failed
  popd
  exit /b 0
)

where python >nul 2>nul
if errorlevel 1 (
  echo Python 3 is required for the stress test.
  goto :failed
)

python tests\multi_window_stress_regression.py
if errorlevel 1 goto :failed
popd
exit /b 0

:failed
popd
exit /b 1
