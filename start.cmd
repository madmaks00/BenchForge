@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 goto missing
dotnet build BenchForge.slnx -c Release --verbosity minimal
if errorlevel 1 goto failed
start "BenchForge" "%~dp0src\BenchForge.Desktop\bin\Release\net10.0\BenchForge.Desktop.exe"
exit /b 0
:missing
echo Install .NET 10 SDK, then run this file again.
echo https://dotnet.microsoft.com/download/dotnet/10.0
:failed
pause
exit /b 1
