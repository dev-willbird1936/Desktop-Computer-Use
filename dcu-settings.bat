@echo off
rem Copyright (c) 2026 dev-willbird1936 - https://github.com/dev-willbird1936/Desktop-Computer-Use
rem Licensed under MIT. See LICENSE. Keep this notice when redistributing.
rem DCU settings — opens the settings page in your browser.
rem Close the console window (or Ctrl+C) when you're done.
title DCU Settings
where py >nul 2>nul && (py -3 "%~dp0scripts\settings_server.py" & goto :eof)
where python >nul 2>nul && (python "%~dp0scripts\settings_server.py" & goto :eof)
echo Python not found on PATH. Install Python 3 or run manually:
echo   python "%~dp0scripts\settings_server.py"
pause
