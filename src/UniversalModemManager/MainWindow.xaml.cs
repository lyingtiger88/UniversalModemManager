using System.Diagnostics;
using System.Net.NetworkInformation;
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
    private readonly ContactStore _contactStore = new();

    private ModemProfile? _profile;
    private IModemAdapter? _activeAdapter;
    private List<Contact> _contacts = [];
    private List<SmsConversation> _smsConversations = [];
    private Guid? _editingContactId;
    private string? _selectedSmsRecipientNumber;

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

        _contacts = await _contactStore.LoadAsync();
        RefreshContactsList();

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
            ApplyAdapterCapabilities(_activeAdapter);
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

    private async void RootNavigation_SelectionChanged(
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
                ShowPage(NetworkPage);
                await RefreshNetworkAsync(showSuccess: false);
                break;

            case "wifi":
                ShowPage(WifiPage);
                await RefreshWifiAsync(showSuccess: false);
                break;

            case "clients":
                ShowPage(ClientsPage);
                await RefreshClientsAsync(showSuccess: false);
                break;

            case "traffic":
                ShowPage(TrafficPage);
                await RefreshTrafficAsync(showSuccess: false);
                break;

            case "contacts":
                ShowPage(ContactsPage);
                RefreshContactsList();
                break;

            case "sms":
                if (!SmsNavigationItem.IsEnabled)
                    return;

                ShowPage(SmsPage);
                await RefreshSmsAsync(showSuccess: false);
                break;

            case "diagnostics":
                ShowPage(DiagnosticsPage);
                break;
        }
    }

    private void ShowPage(FrameworkElement target)
    {
        DashboardPage.Visibility = Visibility.Collapsed;
        ModemPage.Visibility = Visibility.Collapsed;
        NetworkPage.Visibility = Visibility.Collapsed;
        WifiPage.Visibility = Visibility.Collapsed;
        ClientsPage.Visibility = Visibility.Collapsed;
        TrafficPage.Visibility = Visibility.Collapsed;
        SmsPage.Visibility = Visibility.Collapsed;
        ContactsPage.Visibility = Visibility.Collapsed;
        DiagnosticsPage.Visibility = Visibility.Collapsed;
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
            ApplyAdapterCapabilities(adapter);

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

            var identity =
                await adapter.GetIdentityAsync(candidate);

            var detectedManufacturer =
                identity?.Manufacturer ??
                result.DetectedManufacturer ??
                (candidate.Manufacturer == "Auto detect"
                    ? "Generic"
                    : candidate.Manufacturer);

            var detectedModel =
                identity?.Model ??
                result.DetectedModel ??
                (candidate.Model == "Auto detect"
                    ? "Unknown"
                    : candidate.Model);

            if (IsPlaceholderModel(detectedModel))
                detectedModel = "Modem";

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
            ApplyAdapterCapabilities(adapter);

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

    private async void RefreshNetwork_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshNetworkAsync(showSuccess: true);
    }

    private async Task RefreshNetworkAsync(
        bool showSuccess)
    {
        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemNetworkProvider provider)
            {
                SetNetworkInfo(
                    InfoBarSeverity.Warning,
                    "Network data unavailable",
                    $"{context.Adapter.DisplayName} does not currently implement live network telemetry.");
                return;
            }

            var data = await provider.GetNetworkAsync(context.Candidate);

            NetworkConnectionText.Text =
                data.ConnectionState ?? "—";

            NetworkTypeText.Text =
                data.NetworkType ?? "—";

            NetworkOperatorText.Text =
                data.OperatorName ?? "—";

            NetworkOperatorCodeText.Text =
                string.IsNullOrWhiteSpace(data.OperatorCode)
                    ? "Operator code —"
                    : data.OperatorCode;

            NetworkSignalText.Text =
                data.SignalPercent is null
                    ? "—"
                    : $"{data.SignalPercent}%";

            NetworkRoamingText.Text =
                data.IsRoaming is null
                    ? "Roaming —"
                    : data.IsRoaming.Value
                        ? "Roaming"
                        : "Home network";

            NetworkWanIpText.Text =
                data.WanIp ?? "—";

            NetworkRsrpText.Text =
                data.Rsrp ?? "—";

            NetworkRsrqText.Text =
                data.Rsrq ?? "—";

            NetworkSinrText.Text =
                data.Sinr ?? "—";

            NetworkBatteryText.Text =
                data.BatteryPercent is null
                    ? "—"
                    : $"{data.BatteryPercent}%";

            if (showSuccess)
            {
                SetNetworkInfo(
                    InfoBarSeverity.Success,
                    "Network data refreshed",
                    $"Live network information was read using {context.Adapter.DisplayName}.");
            }
            else
            {
                NetworkInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            SetNetworkInfo(
                InfoBarSeverity.Error,
                "Could not read network data",
                ex.Message);
        }
    }

    private void SetNetworkInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        NetworkInfoBar.Severity = severity;
        NetworkInfoBar.Title = title;
        NetworkInfoBar.Message = message;
        NetworkInfoBar.IsOpen = true;
    }

    private async void RefreshWifi_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshWifiAsync(showSuccess: true);
    }

    private async Task RefreshWifiAsync(
        bool showSuccess)
    {
        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemWifiProvider provider)
            {
                SetWifiInfo(
                    InfoBarSeverity.Warning,
                    "Wi-Fi data unavailable",
                    $"{context.Adapter.DisplayName} does not currently implement Wi-Fi configuration reads.");
                return;
            }

            var data = await provider.GetWifiAsync(context.Candidate);

            WifiSsidText.Text = data.Ssid ?? "—";
            WifiEnabledText.Text = data.EnabledDisplay;
            WifiHiddenText.Text = data.HiddenDisplay;
            WifiModeText.Text = data.Mode ?? "—";
            WifiChannelText.Text = data.Channel ?? "—";
            WifiMaxClientsText.Text =
                data.MaxClients?.ToString() ?? "—";
            WifiIsolationText.Text =
                data.IsolationDisplay;

            WifiSsidEditBox.Text = data.Ssid ?? string.Empty;
            WifiChannelEditBox.Text = data.Channel ?? string.Empty;

            if (data.Enabled is not null)
                WifiEnabledSwitch.IsOn = data.Enabled.Value;

            if (data.Hidden is not null)
                WifiHiddenSwitch.IsOn = data.Hidden.Value;

            if (data.ClientIsolation is not null)
                WifiIsolationSwitch.IsOn = data.ClientIsolation.Value;

            WifiMaxClientsNumberBox.Value =
                data.MaxClients is null
                    ? double.NaN
                    : data.MaxClients.Value;

            if (showSuccess)
            {
                SetWifiInfo(
                    InfoBarSeverity.Success,
                    "Wi-Fi data refreshed",
                    $"Wireless settings were read using {context.Adapter.DisplayName}.");
            }
            else
            {
                WifiInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            SetWifiInfo(
                InfoBarSeverity.Error,
                "Could not read Wi-Fi data",
                ex.Message);
        }
    }

    private async void ApplyWifiSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        ApplyWifiSettingsButton.IsEnabled = false;

        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemWifiProvider provider)
            {
                SetWifiInfo(
                    InfoBarSeverity.Warning,
                    "Wi-Fi changes unavailable",
                    $"{context.Adapter.DisplayName} does not implement Wi-Fi configuration changes.");
                return;
            }

            int? maxClients = null;
            if (!double.IsNaN(WifiMaxClientsNumberBox.Value))
                maxClients = (int)Math.Round(WifiMaxClientsNumberBox.Value);

            var request = new WifiUpdateRequest(
                Ssid: WifiSsidEditBox.Text,
                Enabled: WifiEnabledSwitch.IsOn,
                Hidden: WifiHiddenSwitch.IsOn,
                Channel: string.IsNullOrWhiteSpace(WifiChannelEditBox.Text)
                    ? null
                    : WifiChannelEditBox.Text.Trim(),
                MaxClients: maxClients,
                ClientIsolation: WifiIsolationSwitch.IsOn);

            await provider.UpdateWifiAsync(
                context.Candidate,
                request);

            SetWifiInfo(
                InfoBarSeverity.Success,
                "Wi-Fi settings applied",
                "The modem accepted the new settings. If SSID, channel or radio state changed, your current Wi-Fi connection may reconnect.");

            await RefreshWifiAsync(showSuccess: false);
        }
        catch (Exception ex)
        {
            SetWifiInfo(
                InfoBarSeverity.Error,
                "Could not change Wi-Fi settings",
                ex.Message);
        }
        finally
        {
            ApplyWifiSettingsButton.IsEnabled = true;
        }
    }

    private void SetWifiInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        WifiInfoBar.Severity = severity;
        WifiInfoBar.Title = title;
        WifiInfoBar.Message = message;
        WifiInfoBar.IsOpen = true;
    }

    private async void RefreshClients_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshClientsAsync(showSuccess: true);
    }

    private async Task RefreshClientsAsync(
        bool showSuccess)
    {
        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemConnectedDevicesProvider provider)
            {
                SetClientsInfo(
                    InfoBarSeverity.Warning,
                    "Client list unavailable",
                    $"{context.Adapter.DisplayName} does not implement connected-device discovery.");
                return;
            }

            var clients =
                await provider.GetConnectedDevicesAsync(
                    context.Candidate);

            ClientsListView.ItemsSource = clients;
            ClientsCountText.Text =
                $"{clients.Count} device{(clients.Count == 1 ? string.Empty : "s")}";

            var hasPerClientTraffic =
                clients.Any(x => x.HasTrafficCounters);

            if (showSuccess)
            {
                SetClientsInfo(
                    InfoBarSeverity.Success,
                    "Connected devices refreshed",
                    hasPerClientTraffic
                        ? $"{clients.Count} connected device(s) loaded, including per-device traffic counters where reported."
                        : $"{clients.Count} connected device(s) loaded. This firmware does not expose per-device byte counters, so traffic fields remain N/A.");
            }
            else
            {
                ClientsInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            SetClientsInfo(
                InfoBarSeverity.Error,
                "Could not read connected devices",
                ex.Message);
        }
    }

    private void SetClientsInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        ClientsInfoBar.Severity = severity;
        ClientsInfoBar.Title = title;
        ClientsInfoBar.Message = message;
        ClientsInfoBar.IsOpen = true;
    }

    private async void RefreshTraffic_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshTrafficAsync(showSuccess: true);
    }

    private async Task RefreshTrafficAsync(
        bool showSuccess)
    {
        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemTrafficProvider provider)
            {
                SetTrafficInfo(
                    InfoBarSeverity.Warning,
                    "Traffic data unavailable",
                    $"{context.Adapter.DisplayName} does not implement modem traffic counters.");
                return;
            }

            var traffic =
                await provider.GetTrafficAsync(
                    context.Candidate);

            var month =
                await provider.GetMonthTrafficAsync(
                    context.Candidate);

            TrafficSessionTimeText.Text =
                TrafficFormat.Duration(
                    traffic.CurrentConnectTimeSeconds);

            TrafficSessionUploadText.Text =
                $"Upload {TrafficFormat.Bytes(traffic.CurrentUploadBytes)}";

            TrafficSessionDownloadText.Text =
                $"Download {TrafficFormat.Bytes(traffic.CurrentDownloadBytes)}";

            TrafficSessionTotalText.Text =
                $"Total {TrafficFormat.Bytes(traffic.CurrentTotalBytes)}";

            TrafficUploadRateText.Text =
                TrafficFormat.Rate(
                    traffic.CurrentUploadRateBytesPerSecond);

            TrafficDownloadRateText.Text =
                TrafficFormat.Rate(
                    traffic.CurrentDownloadRateBytesPerSecond);

            TrafficMonthTimeText.Text =
                TrafficFormat.Duration(
                    month.DurationSeconds);

            TrafficMonthUploadText.Text =
                $"Upload {TrafficFormat.Bytes(month.UploadBytes)}";

            TrafficMonthDownloadText.Text =
                $"Download {TrafficFormat.Bytes(month.DownloadBytes)}";

            TrafficMonthTotalText.Text =
                $"Total {TrafficFormat.Bytes(month.TotalBytes)}";

            TrafficTotalTimeText.Text =
                TrafficFormat.Duration(
                    traffic.TotalConnectTimeSeconds);

            TrafficTotalUploadText.Text =
                TrafficFormat.Bytes(
                    traffic.TotalUploadBytes);

            TrafficTotalDownloadText.Text =
                TrafficFormat.Bytes(
                    traffic.TotalDownloadBytes);

            TrafficGrandTotalText.Text =
                TrafficFormat.Bytes(
                    traffic.TotalBytes);

            if (showSuccess)
            {
                SetTrafficInfo(
                    InfoBarSeverity.Success,
                    "Traffic refreshed",
                    "Session, monthly and lifetime modem counters were updated.");
            }
            else
            {
                TrafficInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            SetTrafficInfo(
                InfoBarSeverity.Error,
                "Could not read traffic data",
                ex.Message);
        }
    }

    private void SetTrafficInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        TrafficInfoBar.Severity = severity;
        TrafficInfoBar.Title = title;
        TrafficInfoBar.Message = message;
        TrafficInfoBar.IsOpen = true;
    }

    private async void RunDiagnostics_Click(
        object sender,
        RoutedEventArgs e)
    {
        RunDiagnosticsButton.IsEnabled = false;
        DiagnosticsInfoBar.IsOpen = false;

        var results = new List<DiagnosticItem>();

        try
        {
            var context = await ResolveFeatureContextAsync();
            var gatewayHost = context.Candidate.Gateway.Host;

            results.Add(new DiagnosticItem(
                "Profile",
                "PASS",
                $"{context.Candidate.Manufacturer} {context.Candidate.Model} • {context.Candidate.Gateway} • {context.Adapter.DisplayName}"));

            var pingWatch = Stopwatch.StartNew();

            try
            {
                using var ping = new Ping();
                var reply =
                    await ping.SendPingAsync(
                        gatewayHost,
                        3000);

                pingWatch.Stop();

                results.Add(new DiagnosticItem(
                    "ICMP gateway",
                    reply.Status == IPStatus.Success
                        ? "PASS"
                        : "WARN",
                    reply.Status == IPStatus.Success
                        ? $"Gateway replied from {reply.Address}."
                        : $"Ping status: {reply.Status}. Some modems block ICMP even when HTTP works.",
                    pingWatch.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                pingWatch.Stop();

                results.Add(new DiagnosticItem(
                    "ICMP gateway",
                    "WARN",
                    $"Ping failed: {ex.Message}. HTTP/API checks will continue.",
                    pingWatch.ElapsedMilliseconds));
            }

            var probeWatch = Stopwatch.StartNew();
            var probe =
                await context.Adapter.ProbeAsync(
                    context.Candidate);
            probeWatch.Stop();

            results.Add(new DiagnosticItem(
                "Adapter probe",
                probe.Reachable ? "PASS" : "FAIL",
                probe.Detail ??
                (probe.Reachable
                    ? "Adapter recognized the modem."
                    : "Adapter could not reach the modem."),
                probeWatch.ElapsedMilliseconds));

            if (context.Adapter is IModemAuthenticationProvider auth)
            {
                var watch = Stopwatch.StartNew();

                try
                {
                    var state =
                        await auth.GetAuthenticationStateAsync(
                            context.Candidate);
                    watch.Stop();

                    results.Add(new DiagnosticItem(
                        "Authentication",
                        state.IsLoggedIn
                            ? "PASS"
                            : state.IsLocked
                                ? "WARN"
                                : "INFO",
                        state.IsLoggedIn
                            ? "Admin session is authenticated."
                            : state.IsLocked
                                ? $"Login is temporarily locked for about {state.RemainingWaitSeconds} second(s)."
                                : state.Detail ?? "Login is available but the session is not authenticated.",
                        watch.ElapsedMilliseconds));
                }
                catch (Exception ex)
                {
                    watch.Stop();

                    results.Add(new DiagnosticItem(
                        "Authentication",
                        "WARN",
                        ex.Message,
                        watch.ElapsedMilliseconds));
                }
            }

            await RunDiagnosticFeatureCheckAsync(
                results,
                "Dashboard API",
                async () =>
                {
                    if (context.Adapter is not IModemDashboardProvider provider)
                        return "Adapter does not implement dashboard telemetry.";

                    var data =
                        await provider.GetDashboardAsync(
                            context.Candidate);

                    return $"Model {data.Model}; network {data.NetworkType ?? "N/A"}; signal {(data.SignalPercent is null ? "N/A" : data.SignalPercent + "%")}.";
                });

            await RunDiagnosticFeatureCheckAsync(
                results,
                "Network API",
                async () =>
                {
                    if (context.Adapter is not IModemNetworkProvider provider)
                        return "Adapter does not implement network telemetry.";

                    var data =
                        await provider.GetNetworkAsync(
                            context.Candidate);

                    return $"{data.ConnectionState ?? "Unknown"}; {data.NetworkType ?? "Unknown"}; WAN {data.WanIp ?? "N/A"}.";
                });

            await RunDiagnosticFeatureCheckAsync(
                results,
                "Wi-Fi API",
                async () =>
                {
                    if (context.Adapter is not IModemWifiProvider provider)
                        return "Adapter does not implement Wi-Fi reads.";

                    var data =
                        await provider.GetWifiAsync(
                            context.Candidate);

                    return $"SSID {data.Ssid ?? "N/A"}; radio {data.EnabledDisplay}; channel {data.Channel ?? "N/A"}.";
                });

            await RunDiagnosticFeatureCheckAsync(
                results,
                "Traffic API",
                async () =>
                {
                    if (context.Adapter is not IModemTrafficProvider provider)
                        return "Adapter does not implement traffic counters.";

                    var data =
                        await provider.GetTrafficAsync(
                            context.Candidate);

                    return $"Session {TrafficFormat.Bytes(data.CurrentTotalBytes)}; lifetime {TrafficFormat.Bytes(data.TotalBytes)}.";
                });

            await RunDiagnosticFeatureCheckAsync(
                results,
                "Client API",
                async () =>
                {
                    if (context.Adapter is not IModemConnectedDevicesProvider provider)
                        return "Adapter does not implement client discovery.";

                    var data =
                        await provider.GetConnectedDevicesAsync(
                            context.Candidate);

                    return $"{data.Count} connected device(s) reported.";
                });

            await RunDiagnosticFeatureCheckAsync(
                results,
                "SMS API",
                async () =>
                {
                    if (context.Adapter is not IModemSmsProvider provider)
                        return "Adapter does not implement SMS.";

                    var counts =
                        await provider.GetSmsCountsAsync(
                            context.Candidate);

                    return $"Inbox {counts.LocalInbox}; unread {counts.LocalUnread}; sent {counts.LocalOutbox}.";
                });

            DiagnosticsListView.ItemsSource = results;

            var failures =
                results.Count(x => x.Status == "FAIL");

            DiagnosticsInfoBar.Severity =
                failures == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning;

            DiagnosticsInfoBar.Title =
                failures == 0
                    ? "Diagnostics completed"
                    : "Diagnostics completed with failures";

            DiagnosticsInfoBar.Message =
                failures == 0
                    ? $"{results.Count} checks completed. Review WARN/INFO rows for firmware limitations."
                    : $"{failures} check(s) failed. Review the results below.";

            DiagnosticsInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            results.Add(new DiagnosticItem(
                "Diagnostics",
                "FAIL",
                ex.Message));

            DiagnosticsListView.ItemsSource = results;

            DiagnosticsInfoBar.Severity =
                InfoBarSeverity.Error;
            DiagnosticsInfoBar.Title =
                "Diagnostics could not complete";
            DiagnosticsInfoBar.Message =
                ex.Message;
            DiagnosticsInfoBar.IsOpen = true;
        }
        finally
        {
            RunDiagnosticsButton.IsEnabled = true;
        }
    }

    private static async Task RunDiagnosticFeatureCheckAsync(
        List<DiagnosticItem> results,
        string name,
        Func<Task<string>> check)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var detail = await check();
            watch.Stop();

            var unsupported =
                detail.Contains(
                    "does not implement",
                    StringComparison.OrdinalIgnoreCase);

            results.Add(new DiagnosticItem(
                name,
                unsupported ? "INFO" : "PASS",
                detail,
                watch.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            watch.Stop();

            results.Add(new DiagnosticItem(
                name,
                "FAIL",
                ex.Message,
                watch.ElapsedMilliseconds));
        }
    }

    private async void RefreshSms_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshSmsAsync(showSuccess: true);
    }

    private async void SmsBoxComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_profile is null && _activeAdapter is null)
            return;

        await RefreshSmsAsync(showSuccess: false);
    }

    private async Task RefreshSmsAsync(
        bool showSuccess)
    {
        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemSmsProvider provider)
            {
                SetSmsInfo(
                    InfoBarSeverity.Warning,
                    "SMS unavailable",
                    $"{context.Adapter.DisplayName} does not currently implement the modem SMS API.");
                return;
            }

            var counts = await provider.GetSmsCountsAsync(
                context.Candidate);

            var box = GetSelectedSmsBox();

            var messages = await provider.GetSmsMessagesAsync(
                context.Candidate,
                box,
                page: 1,
                readCount: 20);

            SmsCountsText.Text =
                $"Inbox {counts.LocalInbox} • Unread {counts.LocalUnread} • Sent {counts.LocalOutbox} • Draft {counts.LocalDraft}";

            SmsListView.ItemsSource = messages;

            if (showSuccess)
            {
                SetSmsInfo(
                    InfoBarSeverity.Success,
                    "SMS refreshed",
                    $"{messages.Count} message(s) loaded from {box}.");
            }
            else
            {
                SmsInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            SetSmsInfo(
                InfoBarSeverity.Error,
                "Could not read SMS",
                BuildSmsErrorMessage(ex));
        }
    }

    private async void SendSms_Click(
        object sender,
        RoutedEventArgs e)
    {
        SendSmsButton.IsEnabled = false;

        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemSmsProvider provider)
            {
                SetSmsInfo(
                    InfoBarSeverity.Warning,
                    "SMS unavailable",
                    $"{context.Adapter.DisplayName} does not support SMS sending.");
                return;
            }

            await provider.SendSmsAsync(
                context.Candidate,
                SmsPhoneBox.Text,
                SmsMessageBox.Text);

            SmsMessageBox.Text = string.Empty;

            SetSmsInfo(
                InfoBarSeverity.Success,
                "SMS submitted",
                "The modem accepted the message for sending.");

            await RefreshSmsAsync(showSuccess: false);
        }
        catch (Exception ex)
        {
            SetSmsInfo(
                InfoBarSeverity.Error,
                "Could not send SMS",
                BuildSmsErrorMessage(ex));
        }
        finally
        {
            SendSmsButton.IsEnabled = true;
        }
    }

    private async void MarkSmsRead_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SmsListView.SelectedItem is not SmsMessage message)
        {
            SetSmsInfo(
                InfoBarSeverity.Informational,
                "Select a message",
                "Choose an SMS from the list first.");
            return;
        }

        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemSmsProvider provider)
                return;

            await provider.MarkSmsReadAsync(
                context.Candidate,
                message.Index);

            await RefreshSmsAsync(showSuccess: false);

            SetSmsInfo(
                InfoBarSeverity.Success,
                "Message updated",
                "The selected SMS was marked as read.");
        }
        catch (Exception ex)
        {
            SetSmsInfo(
                InfoBarSeverity.Error,
                "Could not update SMS",
                BuildSmsErrorMessage(ex));
        }
    }

    private async void DeleteSms_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SmsListView.SelectedItem is not SmsMessage message)
        {
            SetSmsInfo(
                InfoBarSeverity.Informational,
                "Select a message",
                "Choose an SMS from the list first.");
            return;
        }

        try
        {
            var context = await ResolveFeatureContextAsync();

            if (context.Adapter is not IModemSmsProvider provider)
                return;

            await provider.DeleteSmsAsync(
                context.Candidate,
                message.Index);

            await RefreshSmsAsync(showSuccess: false);

            SetSmsInfo(
                InfoBarSeverity.Success,
                "Message deleted",
                "The modem confirmed SMS deletion.");
        }
        catch (Exception ex)
        {
            SetSmsInfo(
                InfoBarSeverity.Error,
                "Could not delete SMS",
                BuildSmsErrorMessage(ex));
        }
    }

    private SmsBoxType GetSelectedSmsBox()
    {
        var content =
            (SmsBoxComboBox.SelectedItem as ComboBoxItem)
            ?.Content
            ?.ToString();

        return content switch
        {
            "Sent" => SmsBoxType.Sent,
            "Draft" => SmsBoxType.Draft,
            _ => SmsBoxType.Inbox
        };
    }

    private void SetSmsInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        SmsInfoBar.Severity = severity;
        SmsInfoBar.Title = title;
        SmsInfoBar.Message = message;
        SmsInfoBar.IsOpen = true;
    }

    private static string BuildSmsErrorMessage(
        Exception ex)
    {
        if (ex.Message.Contains(
                "108006",
                StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains(
                "login is required",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Admin login is required for SMS on this modem. Open Modem profile, log in, then return to SMS.";
        }

        return ex.Message;
    }

    private async Task<FeatureContext>
        ResolveFeatureContextAsync()
    {
        if (_profile is not null)
        {
            var adapter =
                _activeAdapter ??
                await ResolveProfileAdapterAsync();

            return new FeatureContext(
                adapter,
                CandidateFromProfile());
        }

        var candidate =
            TryBuildCandidate() ??
            throw new InvalidOperationException(
                "Register or select a modem first.");

        var resolved =
            await ResolveAdapterForCandidateAsync(candidate);

        if (!resolved.Probe.Reachable)
        {
            throw new InvalidOperationException(
                resolved.Probe.Detail ??
                "The modem did not respond.");
        }

        _activeAdapter = resolved.Adapter;

        return new FeatureContext(
            resolved.Adapter,
            candidate);
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

    private sealed record FeatureContext(
        IModemAdapter Adapter,
        ModemCandidate Candidate);
}
