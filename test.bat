@echo off
rem Copyright (c) 2026 dev-willbird1936
setlocal
title DCU Tests
pushd "%~dp0"

dotnet test "tests\ShadowUse.Tests\ShadowUse.Tests.csproj" -c Release
if errorlevel 1 (
  popd
  exit /b 1
)

where py >nul 2>nul
if not errorlevel 1 (
  py -3 -m unittest tests.test_bench_native_structs tests.test_settings_server -v
  if errorlevel 1 (
    popd
    exit /b 1
  )
  popd
  exit /b 0
)

where python >nul 2>nul
if errorlevel 1 (
  echo Python 3 is required for the harness tests.
  popd
  exit /b 1
)

python -m unittest tests.test_bench_native_structs tests.test_settings_server -v
set "dcu_test_result=%errorlevel%"
popd
exit /b %dcu_test_result%
