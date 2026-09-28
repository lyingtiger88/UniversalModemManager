namespace UniversalModemManager.Models;

public sealed class ModemProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "My modem";
    public string Manufacturer { get; set; } = "Generic";
    public string Model { get; set; } = "Unknown";
    public string Gateway { get; set; } = "192.168.1.1";
    public string AdapterId { get; set; } = "generic.http";
    public string? Fingerprint { get; set; }
    public bool IsLocked { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
}
