namespace UniversalModemManager.Models;

public sealed record WifiUpdateRequest(
    string? Ssid,
    bool? Enabled,
    bool? Hidden,
    string? Channel,
    int? MaxClients,
    bool? ClientIsolation);
