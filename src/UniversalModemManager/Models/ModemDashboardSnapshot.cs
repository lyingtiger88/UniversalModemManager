namespace UniversalModemManager.Models;

public sealed record ModemDashboardSnapshot(
    string Model,
    string? Firmware,
    string? ConnectionState,
    string? NetworkType,
    string? WanIp,
    int? SignalPercent,
    int? ConnectedClients,
    int? BatteryPercent,
    string? Ssid,
    string? Rsrp,
    string? Rsrq,
    string? Sinr);
