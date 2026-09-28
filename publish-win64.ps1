$ErrorActionPreference = 'Stop'

Write-Host 'Publishing Universal Modem Manager for Windows x64...' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install .NET 9 SDK and try again.'
}

$project = '.\src\UniversalModemManager\UniversalModemManager.csproj'

dotnet restore $project -r win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --self-contained true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$out = Join-Path $PSScriptRoot 'src\UniversalModemManager\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish'
Write-Host ''
Write-Host 'Publish completed successfully.' -ForegroundColor Green
Write-Host "Output: $out"

if (Test-Path $out) {
    Start-Process explorer.exe $out
}
