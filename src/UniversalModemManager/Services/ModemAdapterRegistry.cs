using UniversalModemManager.Adapters.Generic;
using UniversalModemManager.Adapters.Huawei;
using UniversalModemManager.Core;

namespace UniversalModemManager.Services;

public sealed class ModemAdapterRegistry
{
    private readonly IReadOnlyList<IModemAdapter> _adapters =
    [
        new HuaweiHiLinkAdapter(),
        new HuaweiHg532dAdapter(),
        new GenericHttpAdapter()
    ];

    public IReadOnlyList<IModemAdapter> Adapters => _adapters;

    public IModemAdapter? Find(string id) =>
        _adapters.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IModemAdapter? FindByDisplayName(string displayName) =>
        _adapters.FirstOrDefault(x =>
            x.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

    public IModemAdapter Generic =>
        Find("generic.http") ?? _adapters[^1];

    public async Task<AdapterDetectionResult> DetectAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        foreach (var adapter in _adapters.Where(x => x.Id != "generic.http"))
        {
            var probe = await adapter.ProbeAsync(candidate, cancellationToken);
            if (probe.Reachable)
                return new AdapterDetectionResult(adapter, probe);
        }

        var genericProbe = await Generic.ProbeAsync(candidate, cancellationToken);
        return new AdapterDetectionResult(Generic, genericProbe);
    }
}

public sealed record AdapterDetectionResult(
    IModemAdapter Adapter,
    ModemProbeResult Probe);
