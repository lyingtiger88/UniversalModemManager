# Universal Modem Manager

A native Windows modem-management application designed around pluggable modem adapters instead of one hard-coded router family.

## UI stack

- C# / .NET 9
- WinUI 3
- Windows App SDK 2.4.0
- Fluent Design / Windows 11-style shell
- Mica system backdrop
- NavigationView-based layout
- Light/Dark theme inherited from Windows

## Architecture

```text
src/UniversalModemManager/
├─ Adapters/
│  └─ Generic/
├─ Core/
├─ Models/
├─ Services/
├─ App.xaml
├─ MainWindow.xaml
└─ UniversalModemManager.csproj
```

Each modem family implements `IModemAdapter`. The shell and profile system do not depend on a specific Huawei, ZTE, TP-Link, D-Link, Tenda, or other API.

## Modem registration flow

1. Select manufacturer and model.
2. Enter or auto-detect the gateway.
3. Probe the modem.
4. Register the profile.
5. Lock the profile.

When locked, the application will not silently switch to another modem. Changing the selected modem requires explicitly unlocking the profile.

The first scaffold includes a generic HTTP probe. Vendor-specific adapters will progressively add identity, signal, SMS, Wi-Fi clients, APN, traffic, band/network controls and other capabilities.

## Build

Requirements:

- Windows 10 1809 or later; Windows 11 recommended
- .NET 9 SDK
- Visual Studio with Windows application development tools, or equivalent MSBuild tooling

```powershell
cd src\UniversalModemManager
dotnet restore
dotnet build -c Release
```

The project is currently unpackaged and uses the Windows App SDK runtime self-contained deployment option for easier testing.


## Current functional Huawei HiLink features

The first production adapter now supports:

- Live dashboard and network telemetry
- Admin authentication with HiLink session/CSRF handling
- Wi-Fi settings read and update
- Connected Wi-Fi client discovery
- Session, monthly and lifetime traffic counters
- SMS inbox/sent/draft browsing, send, mark-read and delete
- Real diagnostics covering gateway ping, adapter probe, authentication and supported APIs

Per-client traffic is shown only when firmware exposes real counters. Unsupported values remain N/A.
