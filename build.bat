@echo off
REM Build the tiny TrayHider. Output: a single exe in the sibling build folder.
setlocal
cd /d "%~dp0"
set "SDK=C:\Program Files\dotnet\sdk\10.0.103\Roslyn\bincore\csc.dll"
if not exist "%SDK%" set "SDK=C:\Program Files\dotnet\sdk\8.0.301\Roslyn\bincore\csc.dll"
if not exist "%~dp0..\Брвы" mkdir "%~dp0..\Брвы"
dotnet "%SDK%" "@%~dp0build.rsp" /out:"%~dp0..\Брвы\TrayHider.exe"
endlocal
