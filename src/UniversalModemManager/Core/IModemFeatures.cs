using UniversalModemManager.Models;

namespace UniversalModemManager.Core;

public interface IModemDashboardProvider
{
    Task<ModemDashboardSnapshot> GetDashboardAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface IModemAuthenticationProvider
{
    Task<ModemAuthenticationState> GetAuthenticationStateAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<ModemLoginResult> LoginAsync(
        ModemCandidate candidate,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed record ModemAuthenticationState(
    bool Supported,
    bool IsLoggedIn,
    bool IsLocked,
    int RemainingWaitSeconds,
    string? Detail = null);

public sealed record ModemLoginResult(
    bool Success,
    string Message,
    ModemAuthenticationState State);


public interface IModemNetworkProvider
{
    Task<ModemNetworkSnapshot> GetNetworkAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface IModemWifiProvider
{
    Task<ModemWifiSnapshot> GetWifiAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);

    Task UpdateWifiAsync(
        ModemCandidate candidate,
        WifiUpdateRequest request,
        CancellationToken cancellationToken = default);
}

public interface IModemSmsProvider
{
    Task<SmsCounts> GetSmsCountsAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmsMessage>> GetSmsMessagesAsync(
        ModemCandidate candidate,
        SmsBoxType box,
        int page = 1,
        int readCount = 20,
        CancellationToken cancellationToken = default);

    Task SendSmsAsync(
        ModemCandidate candidate,
        string phone,
        string message,
        CancellationToken cancellationToken = default);

    Task MarkSmsReadAsync(
        ModemCandidate candidate,
        string index,
        CancellationToken cancellationToken = default);

    Task DeleteSmsAsync(
        ModemCandidate candidate,
        string index,
        CancellationToken cancellationToken = default);
}


public interface IModemTrafficProvider
{
    Task<TrafficStatistics> GetTrafficAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<MonthTrafficStatistics> GetMonthTrafficAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface IModemConnectedDevicesProvider
{
    Task<IReadOnlyList<ConnectedDevice>> GetConnectedDevicesAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default);
}
