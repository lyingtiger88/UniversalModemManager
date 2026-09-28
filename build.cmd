@echo off
setlocal
cd /d "%~dp0"

echo Building Universal Modem Manager...
dotnet restore ".\src\UniversalModemManager\UniversalModemManager.csproj"
if errorlevel 1 goto :fail

dotnet build ".\src\UniversalModemManager\UniversalModemManager.csproj" -c Release -p:Platform=x64
if errorlevel 1 goto :fail

echo.
echo Build completed successfully.
pause
exit /b 0

:fail
echo.
echo Build failed. See the error output above.
pause
exit /b 1
