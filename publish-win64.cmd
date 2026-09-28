@echo off
setlocal
cd /d "%~dp0"

echo Publishing Universal Modem Manager for Windows x64...
dotnet restore ".\src\UniversalModemManager\UniversalModemManager.csproj" -r win-x64
if errorlevel 1 goto :fail

dotnet publish ".\src\UniversalModemManager\UniversalModemManager.csproj" -c Release -r win-x64 -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --self-contained true
if errorlevel 1 goto :fail

set "OUT=%CD%\src\UniversalModemManager\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish"
echo.
echo Publish completed successfully.
echo Output: %OUT%
if exist "%OUT%" explorer "%OUT%"
pause
exit /b 0

:fail
echo.
echo Publish failed. See the error output above.
pause
exit /b 1
