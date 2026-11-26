@echo off
setlocal
cd /d "%~dp0"
set "compiler=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" exit /b 2
"%compiler%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32manifest:app.manifest /win32icon:FrameDesk.ico /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /out:FrameDesk.exe *.cs
exit /b %errorlevel%
