namespace UniversalModemManager.Models;

public sealed record DiagnosticItem(
    string Test,
    string Status,
    string Detail,
    long? DurationMs = null)
{
    public string DurationDisplay =>
        DurationMs is null ? "—" : $"{DurationMs} ms";
}
