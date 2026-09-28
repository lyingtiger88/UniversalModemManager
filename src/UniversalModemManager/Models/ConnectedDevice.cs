namespace UniversalModemManager.Models;

public sealed record ConnectedDevice(
    string HostName,
    string IpAddress,
    string MacAddress,
    long? AssociatedTimeSeconds = null,
    long? UploadBytes = null,
    long? DownloadBytes = null)
{
    public string ConnectedTimeDisplay =>
        TrafficFormat.Duration(AssociatedTimeSeconds);

    public string UploadDisplay =>
        TrafficFormat.Bytes(UploadBytes);

    public string DownloadDisplay =>
        TrafficFormat.Bytes(DownloadBytes);

    public string TotalUsageDisplay =>
        UploadBytes is null && DownloadBytes is null
            ? "N/A"
            : TrafficFormat.Bytes((UploadBytes ?? 0) + (DownloadBytes ?? 0));

    public bool HasTrafficCounters =>
        UploadBytes is not null || DownloadBytes is not null;
}
