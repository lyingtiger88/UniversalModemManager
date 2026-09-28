$ErrorActionPreference = 'Stop'

Write-Host 'Building Universal Modem Manager...' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install .NET 9 SDK and try again.'
}

dotnet restore .\src\UniversalModemManager\UniversalModemManager.csproj
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

dotnet build .\src\UniversalModemManager\UniversalModemManager.csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

Write-Host 'Build completed successfully.' -ForegroundColor Green
