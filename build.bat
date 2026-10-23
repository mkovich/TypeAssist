@echo off
rem Builds TypeAssist.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No installs needed.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /win32icon:assets\app.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll ^
  /resource:data\words_fa.tsv.gz,words_fa.tsv.gz /resource:data\next_fa.tsv.gz,next_fa.tsv.gz ^
  /resource:data\words_en.tsv.gz,words_en.tsv.gz /resource:data\next_en.tsv.gz,next_en.tsv.gz ^
  /resource:data\fix_en.tsv.gz,fix_en.tsv.gz ^
  /out:TypeAssist.exe src\Engine\*.cs src\Win\*.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built TypeAssist.exe
