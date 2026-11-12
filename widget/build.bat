@echo off
chcp 65001 >nul
rem Grass Widget 빌드 — Windows에 기본으로 있는 .NET Framework 4 컴파일러(csc.exe)를 사용
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:GrassWidget.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll GrassWidget.cs
if errorlevel 1 (
  echo 빌드 실패
  pause
  exit /b 1
)
echo 빌드 완료: %~dp0GrassWidget.exe
