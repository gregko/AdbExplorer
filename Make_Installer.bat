@echo off
setlocal

pushd "%~dp0" || exit /b 1

dotnet build -c Release
if errorlevel 1 exit /b 1

"C:\Program Files (x86)\NSIS\makensis.exe" "%~dp0Installer.nsi"
exit /b %ERRORLEVEL%
