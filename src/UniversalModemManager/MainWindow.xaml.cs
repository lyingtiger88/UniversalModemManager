using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using UniversalModemManager.Core;
using UniversalModemManager.Models;
using UniversalModemManager.Services;

namespace UniversalModemManager;

public sealed partial class MainWindow : Window
{
    private readonly ModemProfileStore _profileStore = new();
    private readonly ModemAdapterRegistry _adapterRegistry = new();

    private ModemProfile? _profile;
    private IModemAdapter? _activeAdapter;

    private readonly Dictionary<string, string[]> _models =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto detect"] = ["Auto detect"],
            ["Huawei"] = ["Auto detect", "E5573 / E5573Cs", "E5577", "B315", "B525", "B612", "B818", "Other / Unknown"],
            ["ZTE"] = ["Auto detect", "MF286", "MF293", "MC801", "MC888", "Other / Unknown"],
            ["TP-Link"] = ["Auto detect", "MR600", "MR6400", "M7350", "Other / Unknown"],
            ["D-Link"] = ["Auto detect", "Other / Unknown"],
            ["Tenda"] = ["Auto detect", "Other / Unknown"],
            ["Generic"] = ["Auto detect", "Other / Unknown"]
        };

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1280, 820));

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is cosmetic; the app remains usable if the OS cannot provide it.
        }

        BrandComboBox.ItemsSource = _models.Keys.ToList();
        BrandComboBox.SelectedItem = "Auto detect";

        AdapterComboBox.ItemsSource = new[] { "Auto detect" }
            .Concat(_adapterRegistry.Adapters.Select(x => x.DisplayName))
            .ToList();
        AdapterComboBox.SelectedItem = "Auto detect";

        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(
        object sender,
        WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;

        _profile = await _profileStore.LoadAsync();
        ApplyProfileToUi();

        if (_profile is null)
        {
            SetAuthenticationStateText(
                "Not checked",
                "Register a modem or probe one to enable adapter-specific login.");
            return;
        }

        try
        {
            _activeAdapter = await ResolveProfileAdapterAsync();
            ApplyProfileToUi();
            await RefreshAuthenticationUiAsync(
                _activeAdapter,
                CandidateFromProfile());
            await RefreshLiveDashboardAsync(showFeedback: false);
        }
        catch (Exception ex)
        {
            DashboardStatusBar.IsOpen = true;
            DashboardStatusBar.Severity = InfoBarSeverity.Warning;
            DashboardStatusBar.Title = "Registered modem is not responding";
            DashboardStatusBar.Message = ex.Message;
        }
    }

    private void BrandComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var brand = BrandComboBox.SelectedItem?.ToString() ?? "Auto detect";

        ModelComboBox.ItemsSource =
            _models.TryGetValue(brand, out var models)
                ? models
                : ["Auto detect", "Other / Unknown"];

        ModelComboBox.SelectedIndex = 0;
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowPlaceholder(
                "Settings",
                "Appearance, discovery behavior, credential storage, diagnostics and update preferences will live here.");
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();

        switch (tag)
        {
            case "dashboard":
                ShowPage(DashboardPage);
                break;

            case "modem":
                ShowPage(ModemPage);
                break;

            case "network":
                ShowPlaceholder(
                    "Network",
                    "Network status, WAN address, cellular technology, signal metrics, APN and network-mode controls will appear here when supported.");
                break;

            case "wifi":
                ShowPlaceholder(
                    "Wi-Fi",
                    "SSID, channel, radio state, security, guest network and Wi-Fi configuration will be enabled by the active adapter.");
                break;

            case "clients":
                ShowPlaceholder(
                    "Connected devices",
                    "Per-client IP, MAC, connection time, traffic counters, disconnect, blacklist and local aliases will appear here.");
                break;

            case "traffic":
                ShowPlaceholder(
                    "Traffic",
                    "Session, daily, monthly, quarterly and yearly traffic history will be stored independently of the modem's limited counters.");
                break;

            case "sms":
                ShowPlaceholder(
                    "SMS",
                    "Inbox, sent messages, drafts and SMS composer will activate only for modems exposing a supported SMS API.");
                break;

            case "diagnostics":
                ShowPlaceholder(
                    "Diagnostics",
                    "Unknown modems will be able to produce a safe diagnostic package that excludes passwords, session cookies and message content.");
                break;
        }
    }

    private void ShowPage(FrameworkElement target)
    {
        DashboardPage.Visibility = Visibility.Collapsed;
        ModemPage.Visibility = Visibility.Collapsed;
        PlaceholderPage.Visibility = Visibility.Collapsed;
        target.Visibility = Visibility.Visible;
    }

    private void ShowPlaceholder(
        string title,
        string message)
    {
        PlaceholderTitle.Text = title;
        PlaceholderMessage.Text = message;
        ShowPage(PlaceholderPage);
    }

    private async void ProbeModem_Click(
        object sender,
        RoutedEventArgs e)
    {
        var candidate = TryBuildCandidate();
        if (candidate is null)
            return;

        SetProbeState(
            InfoBarSeverity.Informational,
            "Probing modem",
            "Testing adapter APIs at the selected gateway without changing the saved profile.");

        try
        {
            var resolved = await ResolveAdapterForCandidateAsync(candidate);
            var adapter = resolved.Adapter;
            var result = resolved.Probe;

            if (!result.Reachable)
            {
                _activeAdapter = null;

                SetProbeState(
                    InfoBarSeverity.Error,
                    "Modem not reachable",
                    result.Detail ?? "No supported response was received from the selected gateway.");
                return;
            }

            _activeAdapter = adapter;

            var detected =
                $"{result.DetectedManufacturer ?? "Unknown vendor"}" +
                (string.IsNullOrWhiteSpace(result.DetectedModel)
                    ? string.Empty
                    : $" {result.DetectedModel}");

            SetProbeState(
                result.MatchesSelectedProfile
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                result.MatchesSelectedProfile
                    ? $"{adapter.DisplayName} detected"
                    : "The response may not match the selected profile",
                $"Detected: {detected}. {result.Detail}");

            SelectComboValue(AdapterComboBox, adapter.DisplayName);
            await RefreshAuthenticationUiAsync(adapter, candidate);
        }
        catch (Exception ex)
        {
            SetProbeState(
                InfoBarSeverity.Error,
                "Probe failed",
                ex.Message);
        }
    }

    private async void RegisterModem_Click(
        object sender,
        RoutedEventArgs e)
    {
        var candidate = TryBuildCandidate();
        if (candidate is null)
            return;

        try
        {
            var resolved = await ResolveAdapterForCandidateAsync(candidate);
            var adapter = resolved.Adapter;
            var result = resolved.Probe;

            if (!result.Reachable)
            {
                SetProbeState(
                    InfoBarSeverity.Error,
                    "Registration stopped",
                    result.Detail ?? "The modem did not respond.");
                return;
            }

            if (!result.MatchesSelectedProfile)
            {
                SetProbeState(
                    InfoBarSeverity.Warning,
                    "Identity mismatch",
                    $"The selected profile is {candidate.Manufacturer} {candidate.Model}, but the gateway appears to be {result.DetectedManufacturer ?? "an unknown vendor"} {result.DetectedModel ?? string.Empty}. Change the selection or use Auto detect before registering.");
                return;
            }

            var detectedManufacturer =
                result.DetectedManufacturer ??
                (candidate.Manufacturer == "Auto detect"
                    ? "Generic"
                    : candidate.Manufacturer);

            var detectedModel =
                result.DetectedModel ??
                (candidate.Model == "Auto detect"
                    ? "Unknown"
                    : candidate.Model);

            var fingerprint = BuildFingerprint(
                detectedManufacturer,
                detectedModel,
                candidate.Gateway);

            _profile = new ModemProfile
            {
                DisplayName = string.IsNullOrWhiteSpace(ProfileNameBox.Text)
                    ? $"{detectedManufacturer} {detectedModel}"
                    : ProfileNameBox.Text.Trim(),
                Manufacturer = detectedManufacturer,
                Model = detectedModel,
                Gateway = candidate.Gateway.ToString(),
                AdapterId = adapter.Id,
                Fingerprint = fingerprint,
                IsLocked = LockAfterRegisterSwitch.IsOn,
                LastSeenUtc = DateTime.UtcNow
            };

            _activeAdapter = adapter;

            await _profileStore.SaveAsync(_profile);
            ApplyProfileToUi();

            SetProbeState(
                InfoBarSeverity.Success,
                _profile.IsLocked
                    ? "Modem registered and locked"
                    : "Modem registered",
                $"{_profile.Manufacturer} {_profile.Model} is now using the {adapter.DisplayName} adapter.");

            await RefreshAuthenticationUiAsync(
                adapter,
                CandidateFromProfile());

            await RefreshLiveDashboardAsync(showFeedback: false);
        }
        catch (Exception ex)
        {
            SetProbeState(
                InfoBarSeverity.Error,
                "Registration failed",
                ex.Message);
        }
    }

    private async void UnlockProfile_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_profile is null)
            return;

        _profile.IsLocked = false;
        await _profileStore.SaveAsync(_profile);
        ApplyProfileToUi();

        SetProbeState(
            InfoBarSeverity.Informational,
            "Profile unlocked",
            "You can now select and register another modem.");
    }

    private async void RefreshDashboard_Click(
        object sender,
        RoutedEventArgs e)
    {
        ApplyProfileToUi();
        await RefreshLiveDashboardAsync(showFeedback: true);
    }

    private async void CheckLoginState_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var context = await ResolveAuthenticationContextAsync();
            await RefreshAuthenticationUiAsync(
                context.Adapter,
                context.Candidate,
                showInfoBar: true);
        }
        catch (Exception ex)
        {
            SetLoginInfo(
                InfoBarSeverity.Error,
                "Could not check login state",
                ex.Message);
        }
    }

    private async void AdminLogin_Click(
        object sender,
        RoutedEventArgs e)
    {
        AdminLoginButton.IsEnabled = false;

        try
        {
            var context = await ResolveAuthenticationContextAsync();

            if (context.Adapter is not IModemAuthenticationProvider auth)
            {
                SetAuthenticationStateText(
                    "Not supported",
                    "The selected adapter does not expose an application-managed login API.");

                SetLoginInfo(
                    InfoBarSeverity.Warning,
                    "Login is not available",
                    $"{context.Adapter.DisplayName} currently has no login implementation.");
                return;
            }

            var result = await auth.LoginAsync(
                context.Candidate,
                AdminUsernameBox.Text,
                AdminPasswordBox.Password);

            ApplyAuthenticationState(result.State);

            SetLoginInfo(
                result.Success
                    ? InfoBarSeverity.Success
                    : result.State.IsLocked
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Error,
                result.Success
                    ? "Login successful"
                    : result.State.IsLocked
                        ? "Login temporarily locked"
                        : "Login failed",
                result.Message);

            if (result.Success)
            {
                _activeAdapter = context.Adapter;
                AdminPasswordBox.Password = string.Empty;

                if (_profile is not null)
                    await RefreshLiveDashboardAsync(showFeedback: false);
            }
        }
        catch (Exception ex)
        {
            SetLoginInfo(
                InfoBarSeverity.Error,
                "Login failed",
                ex.Message);
        }
        finally
        {
            AdminLoginButton.IsEnabled = true;
        }
    }

    private async Task RefreshAuthenticationUiAsync(
        IModemAdapter adapter,
        ModemCandidate candidate,
        bool showInfoBar = false)
    {
        if (adapter is not IModemAuthenticationProvider auth)
        {
            SetAuthenticationStateText(
                "Not supported",
                "This adapter does not require or currently implement application-managed authentication.");

            if (showInfoBar)
            {
                SetLoginInfo(
                    InfoBarSeverity.Informational,
                    "No adapter login",
                    $"{adapter.DisplayName} does not expose an application-managed login flow.");
            }

            return;
        }

        try
        {
            var state = await auth.GetAuthenticationStateAsync(candidate);
            ApplyAuthenticationState(state);

            if (showInfoBar)
            {
                SetLoginInfo(
                    state.IsLoggedIn
                        ? InfoBarSeverity.Success
                        : state.IsLocked
                            ? InfoBarSeverity.Warning
                            : InfoBarSeverity.Informational,
                    state.IsLoggedIn
                        ? "Logged in"
                        : state.IsLocked
                            ? "Login is locked"
                            : "Login required",
                    state.Detail ??
                    (state.IsLocked
                        ? $"Wait about {state.RemainingWaitSeconds} second(s) before trying again."
                        : "Enter the modem administrator credentials and press Login."));
            }
        }
        catch (Exception ex)
        {
            SetAuthenticationStateText(
                "Unavailable",
                ex.Message);

            if (showInfoBar)
            {
                SetLoginInfo(
                    InfoBarSeverity.Error,
                    "Login state unavailable",
                    ex.Message);
            }
        }
    }

    private void ApplyAuthenticationState(
        ModemAuthenticationState state)
    {
        if (!state.Supported)
        {
            SetAuthenticationStateText(
                "Not supported",
                state.Detail ?? "The selected firmware does not expose a supported login API.");
            return;
        }

        if (state.IsLoggedIn)
        {
            SetAuthenticationStateText(
                "Logged in",
                state.Detail ?? "The modem session is authenticated.");
            return;
        }

        if (state.IsLocked)
        {
            var text = state.RemainingWaitSeconds > 0
                ? $"Locked • {state.RemainingWaitSeconds}s"
                : "Temporarily locked";

            SetAuthenticationStateText(
                text,
                state.Detail ?? "The modem is temporarily refusing new login attempts.");
            return;
        }

        SetAuthenticationStateText(
            "Login required",
            state.Detail ?? "The modem supports administrator authentication.");
    }

    private void SetAuthenticationStateText(
        string state,
        string detail)
    {
        AuthenticationStateText.Text = state;
        AuthenticationStateText.Tag = detail;
    }

    private void SetLoginInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        LoginInfoBar.Severity = severity;
        LoginInfoBar.Title = title;
        LoginInfoBar.Message = message;
        LoginInfoBar.IsOpen = true;
    }

    private async Task RefreshLiveDashboardAsync(
        bool showFeedback)
    {
        if (_profile is null)
        {
            ResetLiveDashboard();
            return;
        }

        try
        {
            _activeAdapter ??= await ResolveProfileAdapterAsync();

            var candidate = CandidateFromProfile();

            if (_activeAdapter is not IModemDashboardProvider provider)
            {
                ResetLiveDashboard(keepIdentity: true);

                DashboardStatusBar.IsOpen = true;
                DashboardStatusBar.Severity = InfoBarSeverity.Warning;
                DashboardStatusBar.Title = "Limited adapter";
                DashboardStatusBar.Message =
                    $"{_activeAdapter.DisplayName} can reach this router, but it does not expose live modem telemetry yet.";
                return;
            }

            var snapshot = await provider.GetDashboardAsync(candidate);

            DashboardModemText.Text = snapshot.Model;
            DashboardGatewayText.Text = $"Gateway {_profile.Gateway}";

            DashboardConnectionText.Text =
                string.IsNullOrWhiteSpace(snapshot.ConnectionState)
                    ? "—"
                    : snapshot.ConnectionState;

            DashboardConnectionDetailText.Text =
                string.IsNullOrWhiteSpace(snapshot.NetworkType)
                    ? "Network —"
                    : snapshot.NetworkType;

            DashboardSignalText.Text =
                snapshot.SignalPercent is null
                    ? "—"
                    : $"{snapshot.SignalPercent}%";

            DashboardSignalDetailText.Text =
                string.IsNullOrWhiteSpace(snapshot.Rsrp)
                    ? "RSRP —"
                    : $"RSRP {snapshot.Rsrp}";

            DashboardClientsText.Text =
                snapshot.ConnectedClients?.ToString() ?? "—";

            DashboardClientsDetailText.Text =
                string.IsNullOrWhiteSpace(snapshot.Ssid)
                    ? "Wi-Fi clients"
                    : snapshot.Ssid;

            DashboardFirmwareText.Text =
                snapshot.Firmware ?? "—";

            DashboardWanIpText.Text =
                snapshot.WanIp ?? "—";

            DashboardBatteryText.Text =
                snapshot.BatteryPercent is null
                    ? "—"
                    : $"{snapshot.BatteryPercent}%";

            DashboardRsrpRsrqText.Text =
                snapshot.Rsrp is null && snapshot.Rsrq is null
                    ? "—"
                    : $"{snapshot.Rsrp ?? "—"} / {snapshot.Rsrq ?? "—"}";

            DashboardSinrText.Text =
                snapshot.Sinr ?? "—";

            DashboardSsidText.Text =
                snapshot.Ssid ?? "—";

            DashboardStatusBar.IsOpen = showFeedback;
            DashboardStatusBar.Severity = InfoBarSeverity.Success;
            DashboardStatusBar.Title = "Live modem data refreshed";
            DashboardStatusBar.Message =
                $"{_activeAdapter.DisplayName} returned live information from {_profile.Gateway}.";

            await RefreshAuthenticationUiAsync(
                _activeAdapter,
                candidate);
        }
        catch (Exception ex)
        {
            DashboardStatusBar.IsOpen = true;
            DashboardStatusBar.Severity = InfoBarSeverity.Error;
            DashboardStatusBar.Title = "Could not read modem information";
            DashboardStatusBar.Message = ex.Message;
        }
    }

    private void ResetLiveDashboard(
        bool keepIdentity = false)
    {
        if (!keepIdentity)
        {
            DashboardConnectionText.Text = "—";
            DashboardSignalText.Text = "—";
            DashboardClientsText.Text = "—";
        }

        DashboardConnectionDetailText.Text = "No live data";
        DashboardSignalDetailText.Text = "No live data";
        DashboardClientsDetailText.Text = "No live data";
        DashboardFirmwareText.Text = "—";
        DashboardWanIpText.Text = "—";
        DashboardBatteryText.Text = "—";
        DashboardRsrpRsrqText.Text = "—";
        DashboardSinrText.Text = "—";
        DashboardSsidText.Text = "—";
    }

    private async Task<AdapterDetectionResult>
        ResolveAdapterForCandidateAsync(
            ModemCandidate candidate)
    {
        var selected =
            AdapterComboBox.SelectedItem?.ToString() ?? "Auto detect";

        if (selected.Equals(
                "Auto detect",
                StringComparison.OrdinalIgnoreCase))
        {
            return await _adapterRegistry.DetectAsync(candidate);
        }

        var adapter = _adapterRegistry.FindByDisplayName(selected)
                      ?? _adapterRegistry.Generic;

        var probe = await adapter.ProbeAsync(candidate);
        return new AdapterDetectionResult(adapter, probe);
    }

    private async Task<IModemAdapter> ResolveProfileAdapterAsync()
    {
        if (_profile is null)
            return _adapterRegistry.Generic;

        var saved = _adapterRegistry.Find(_profile.AdapterId);

        if (saved is not null &&
            !saved.Id.Equals(
                "generic.http",
                StringComparison.OrdinalIgnoreCase))
        {
            return saved;
        }

        var candidate = CandidateFromProfile();
        var detected = await _adapterRegistry.DetectAsync(candidate);

        if (detected.Probe.Reachable &&
            !detected.Adapter.Id.Equals(
                "generic.http",
                StringComparison.OrdinalIgnoreCase))
        {
            _profile.AdapterId = detected.Adapter.Id;
            _profile.LastSeenUtc = DateTime.UtcNow;
            await _profileStore.SaveAsync(_profile);
            return detected.Adapter;
        }

        return saved ?? _adapterRegistry.Generic;
    }

    private async Task<AuthenticationContext>
        ResolveAuthenticationContextAsync()
    {
        if (_profile?.IsLocked == true)
        {
            var adapter = _activeAdapter
                          ?? await ResolveProfileAdapterAsync();

            return new AuthenticationContext(
                adapter,
                CandidateFromProfile());
        }

        var candidate = TryBuildCandidate()
                        ?? throw new InvalidOperationException(
                            "Enter a valid modem gateway first.");

        if (_activeAdapter is not null)
        {
            var selected =
                AdapterComboBox.SelectedItem?.ToString() ?? "Auto detect";

            if (selected.Equals(
                    "Auto detect",
                    StringComparison.OrdinalIgnoreCase) ||
                selected.Equals(
                    _activeAdapter.DisplayName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new AuthenticationContext(
                    _activeAdapter,
                    candidate);
            }
        }

        var resolved =
            await ResolveAdapterForCandidateAsync(candidate);

        if (!resolved.Probe.Reachable)
        {
            throw new InvalidOperationException(
                resolved.Probe.Detail ??
                "The modem did not respond to the selected adapter.");
        }

        _activeAdapter = resolved.Adapter;

        return new AuthenticationContext(
            resolved.Adapter,
            candidate);
    }

    private ModemCandidate CandidateFromProfile()
    {
        if (_profile is null)
            throw new InvalidOperationException(
                "No modem profile is registered.");

        if (!Uri.TryCreate(
                _profile.Gateway,
                UriKind.Absolute,
                out var gateway))
        {
            throw new InvalidOperationException(
                "The saved modem gateway is invalid.");
        }

        return new ModemCandidate(
            _profile.Manufacturer,
            _profile.Model,
            gateway);
    }

    private ModemCandidate? TryBuildCandidate()
    {
        var brand =
            BrandComboBox.SelectedItem?.ToString() ?? "Auto detect";

        var model =
            ModelComboBox.SelectedItem?.ToString() ?? "Auto detect";

        var rawGateway = GatewayBox.Text.Trim();

        if (!rawGateway.StartsWith(
                "http://",
                StringComparison.OrdinalIgnoreCase) &&
            !rawGateway.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
        {
            rawGateway = "http://" + rawGateway;
        }

        if (!Uri.TryCreate(
                rawGateway,
                UriKind.Absolute,
                out var gateway))
        {
            SetProbeState(
                InfoBarSeverity.Error,
                "Invalid gateway",
                "Enter a valid modem address such as http://192.168.8.1.");
            return null;
        }

        return new ModemCandidate(
            brand,
            model,
            gateway);
    }

    private void ApplyProfileToUi()
    {
        var locked = _profile?.IsLocked == true;

        BrandComboBox.IsEnabled = !locked;
        ModelComboBox.IsEnabled = !locked;
        ProfileNameBox.IsEnabled = !locked;
        GatewayBox.IsEnabled = !locked;
        AdapterComboBox.IsEnabled = !locked;
        LockAfterRegisterSwitch.IsEnabled = !locked;
        RegisterButton.IsEnabled = !locked;
        UnlockProfileButton.IsEnabled = locked;

        if (_profile is null)
        {
            ProfileStateText.Text =
                "No modem has been registered yet.";

            LockBadgeText.Text = "Unlocked";
            LockBadgeIcon.Glyph = "";
            TitleProfileText.Text = "No modem registered";
            DashboardModemText.Text = "Not registered";
            DashboardGatewayText.Text = "Gateway —";

            ProfileInfoBar.IsOpen = true;
            ProfileInfoBar.Severity =
                InfoBarSeverity.Informational;
            ProfileInfoBar.Title =
                "No modem profile is locked";
            ProfileInfoBar.Message =
                "Open Modem profile, choose or auto-detect a modem, then register it.";

            return;
        }

        ProfileStateText.Text =
            $"{_profile.DisplayName}  •  {_profile.Manufacturer} {_profile.Model}\n{_profile.Gateway}";

        LockBadgeText.Text =
            locked ? "Locked" : "Unlocked";

        LockBadgeIcon.Glyph =
            locked ? "" : "";

        TitleProfileText.Text =
            $"{(locked ? "Locked" : "Registered")} • {_profile.Manufacturer} {_profile.Model}";

        DashboardModemText.Text =
            $"{_profile.Manufacturer} {_profile.Model}";

        DashboardGatewayText.Text =
            $"Gateway {_profile.Gateway}";

        ProfileNameBox.Text =
            _profile.DisplayName;

        GatewayBox.Text =
            _profile.Gateway;

        SelectComboValue(
            BrandComboBox,
            _profile.Manufacturer);

        SelectComboValue(
            ModelComboBox,
            _profile.Model);

        var adapter =
            _activeAdapter ??
            _adapterRegistry.Find(_profile.AdapterId);

        if (adapter is not null)
            SelectComboValue(
                AdapterComboBox,
                adapter.DisplayName);

        ProfileInfoBar.IsOpen = true;
        ProfileInfoBar.Severity =
            locked
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;

        ProfileInfoBar.Title =
            locked
                ? "Registered modem profile is locked"
                : "Registered modem profile is unlocked";

        ProfileInfoBar.Message =
            locked
                ? "Automatic adapter work stays bound to this modem profile until you unlock it."
                : "You can change the modem selection and register a different profile.";
    }

    private static void SelectComboValue(
        ComboBox combo,
        string value)
    {
        foreach (var item in combo.Items)
        {
            if (item?.ToString()?.Equals(
                    value,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void SetProbeState(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        ProbeResultBar.Severity = severity;
        ProbeResultBar.Title = title;
        ProbeResultBar.Message = message;
        ProbeResultBar.IsOpen = true;
    }

    private static string BuildFingerprint(
        string manufacturer,
        string model,
        Uri gateway)
    {
        var raw =
            $"{manufacturer.Trim().ToUpperInvariant()}|" +
            $"{model.Trim().ToUpperInvariant()}|" +
            $"{gateway.Host.ToUpperInvariant()}";

        var digest =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(raw));

        return Convert.ToHexString(digest);
    }

    private sealed record AuthenticationContext(
        IModemAdapter Adapter,
        ModemCandidate Candidate);
}
