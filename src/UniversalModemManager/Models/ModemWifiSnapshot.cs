namespace UniversalModemManager.Models;

public sealed record ModemWifiSnapshot(
    string? Ssid,
    bool? Enabled,
    bool? Hidden,
    string? Channel,
    string? Mode,
    int? MaxClients,
    bool? ClientIsolation)
{
    public string EnabledDisplay => Enabled is null ? "N/A" : Enabled.Value ? "On" : "Off";
    public string HiddenDisplay => Hidden is null ? "N/A" : Hidden.Value ? "Hidden" : "Visible";
    public string IsolationDisplay => ClientIsolation is null ? "N/A" : ClientIsolation.Value ? "On" : "Off";
}
