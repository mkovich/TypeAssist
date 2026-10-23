@echo off
rem Builds and runs the engine + input-tracker tests. Exit code 0 = all passed.
cd /d "%~dp0.."
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist bin mkdir bin
"%CSC%" /nologo /codepage:65001 /main:TypeAssist.Tests.Program ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll ^
  /out:bin\TypeAssist.Tests.exe src\Engine\*.cs src\Win\*.cs tests\*.cs
if errorlevel 1 exit /b 1
bin\TypeAssist.Tests.exe data
