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
