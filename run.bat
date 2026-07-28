@echo off
rem Copyright (c) 2026 dev-willbird1936
setlocal
title Desktop Computer Use
dotnet run --project "%~dp0src\ShadowUse\ShadowUse.csproj" -- %*
exit /b %errorlevel%
