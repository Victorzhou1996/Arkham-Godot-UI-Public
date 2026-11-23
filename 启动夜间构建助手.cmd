@echo off
chcp 65001 >nul
title Arkham UI night runner
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\night-runner.ps1"
if errorlevel 1 pause
