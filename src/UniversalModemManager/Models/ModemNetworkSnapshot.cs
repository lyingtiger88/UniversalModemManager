namespace UniversalModemManager.Models;

public sealed record ModemNetworkSnapshot(
    string? ConnectionState,
    string? NetworkType,
    string? OperatorName,
    string? OperatorCode,
    string? WanIp,
    int? SignalPercent,
    string? Rsrp,
    string? Rsrq,
    string? Sinr,
    int? BatteryPercent,
    bool? IsRoaming);
