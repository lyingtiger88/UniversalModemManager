using UniversalModemManager.Adapters.Generic;
using UniversalModemManager.Core;

namespace UniversalModemManager.Services;

public sealed class ModemAdapterRegistry
{
    private readonly IReadOnlyList<IModemAdapter> _adapters =
    [
        new GenericHttpAdapter()
    ];

    public IReadOnlyList<IModemAdapter> Adapters => _adapters;

    public IModemAdapter? Find(string id) =>
        _adapters.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IModemAdapter Generic => _adapters[0];
}
