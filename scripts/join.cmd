@echo off
rem Double-click to play on someone else's RO3Emu server: asks for its address (see start.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" -Join %*
