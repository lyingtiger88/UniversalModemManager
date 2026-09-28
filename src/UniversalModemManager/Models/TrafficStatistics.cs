namespace UniversalModemManager.Models;

public sealed record TrafficStatistics(
    long? CurrentConnectTimeSeconds,
    long? CurrentUploadBytes,
    long? CurrentDownloadBytes,
    long? CurrentUploadRateBytesPerSecond,
    long? CurrentDownloadRateBytesPerSecond,
    long? TotalUploadBytes,
    long? TotalDownloadBytes,
    long? TotalConnectTimeSeconds)
{
    public long? CurrentTotalBytes =>
        CurrentUploadBytes is null && CurrentDownloadBytes is null
            ? null
            : (CurrentUploadBytes ?? 0) + (CurrentDownloadBytes ?? 0);

    public long? TotalBytes =>
        TotalUploadBytes is null && TotalDownloadBytes is null
            ? null
            : (TotalUploadBytes ?? 0) + (TotalDownloadBytes ?? 0);
}

public sealed record MonthTrafficStatistics(
    long? UploadBytes,
    long? DownloadBytes,
    long? DurationSeconds)
{
    public long? TotalBytes =>
        UploadBytes is null && DownloadBytes is null
            ? null
            : (UploadBytes ?? 0) + (DownloadBytes ?? 0);
}

public static class TrafficFormat
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "N/A";

        double value = Math.Max(0, bytes.Value);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0
            ? $"{value:0} {units[index]}"
            : $"{value:0.##} {units[index]}";
    }

    public static string Rate(long? bytesPerSecond) =>
        bytesPerSecond is null ? "N/A" : $"{Bytes(bytesPerSecond)}/s";

    public static string Duration(long? totalSeconds)
    {
        if (totalSeconds is null) return "N/A";

        var seconds = Math.Max(0, totalSeconds.Value);
        var span = TimeSpan.FromSeconds(seconds);
        var totalHours = (long)Math.Floor(span.TotalHours);

        return $"{totalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
