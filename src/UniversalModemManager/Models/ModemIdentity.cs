namespace UniversalModemManager.Models;

public sealed record ModemIdentity(
    string Manufacturer,
    string Model,
    string? Firmware,
    string Gateway,
    string AdapterId,
    string? HardwareId = null);
