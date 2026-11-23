@echo off
chcp 65001 >nul
title 诡镇奇谈 新版
rem 正常游玩：存档与牌组保存在本文件夹的 userdata\
set "GODOT=G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe"
if not exist "%GODOT%" (echo 找不到 Godot 运行时：%GODOT% & pause & exit /b 1)
start "" "%GODOT%" --path "%~dp0app" --rendering-driver opengl3_angle
