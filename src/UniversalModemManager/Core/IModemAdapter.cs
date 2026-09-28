using UniversalModemManager.Models;

namespace UniversalModemManager.Core;

public interface IModemAdapter
{
    string Id { get; }
    string DisplayName { get; }
    ModemCapability Capabilities { get; }

    Task<ModemProbeResult> ProbeAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<ModemIdentity?> GetIdentityAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);
}

public sealed record ModemCandidate(
    string Manufacturer,
    string Model,
    Uri Gateway);

public sealed record ModemProbeResult(
    bool Reachable,
    bool MatchesSelectedProfile,
    string? DetectedManufacturer,
    string? DetectedModel,
    string? Detail = null);
