@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0FPilotQuickLook.exe" "%~dp0FPilotQuickLook.cs"
