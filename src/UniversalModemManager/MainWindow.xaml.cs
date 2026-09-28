using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UniversalModemManager.Core;
using UniversalModemManager.Models;
using UniversalModemManager.Services;

namespace UniversalModemManager;

public sealed partial class MainWindow : Window
{
    private readonly ModemProfileStore _profileStore = new();
    private readonly ModemAdapterRegistry _adapterRegistry = new();
    private ModemProfile? _profile;

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

        AdapterComboBox.ItemsSource = _adapterRegistry.Adapters
            .Select(x => x.DisplayName)
            .ToList();
        AdapterComboBox.SelectedIndex = 0;

        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        _profile = await _profileStore.LoadAsync();
        ApplyProfileToUi();
    }

    private void BrandComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var brand = BrandComboBox.SelectedItem?.ToString() ?? "Auto detect";
        ModelComboBox.ItemsSource = _models.TryGetValue(brand, out var models)
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
            ShowPlaceholder("Settings",
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
                ShowPlaceholder("Network",
                    "Network status, WAN address, cellular technology, signal metrics, APN and network-mode controls will appear here when supported.");
                break;
            case "wifi":
                ShowPlaceholder("Wi-Fi",
                    "SSID, channel, radio state, security, guest network and Wi-Fi configuration will be enabled by the active adapter.");
                break;
            case "clients":
                ShowPlaceholder("Connected devices",
                    "Per-client IP, MAC, connection time, traffic counters, disconnect, blacklist and local aliases will appear here.");
                break;
            case "traffic":
                ShowPlaceholder("Traffic",
                    "Session, daily, monthly, quarterly and yearly traffic history will be stored independently of the modem's limited counters.");
                break;
            case "sms":
                ShowPlaceholder("SMS",
                    "Inbox, sent messages, drafts and SMS composer will activate only for modems exposing a supported SMS API.");
                break;
            case "diagnostics":
                ShowPlaceholder("Diagnostics",
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

    private void ShowPlaceholder(string title, string message)
    {
        PlaceholderTitle.Text = title;
        PlaceholderMessage.Text = message;
        ShowPage(PlaceholderPage);
    }

    private async void ProbeModem_Click(object sender, RoutedEventArgs e)
    {
        var candidate = TryBuildCandidate();
        if (candidate is null)
            return;

        SetProbeState(InfoBarSeverity.Informational,
            "Probing modem",
            "Checking the selected gateway without changing the saved profile.");

        var adapter = _adapterRegistry.Generic;
        var result = await adapter.ProbeAsync(candidate);

        if (!result.Reachable)
        {
            SetProbeState(InfoBarSeverity.Error,
                "Modem not reachable",
                result.Detail ?? "No response was received from the selected gateway.");
            return;
        }

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
                ? "Modem responded"
                : "The response may not match the selected profile",
            $"Detected: {detected}. {result.Detail}");
    }

    private async void RegisterModem_Click(object sender, RoutedEventArgs e)
    {
        var candidate = TryBuildCandidate();
        if (candidate is null)
            return;

        var adapter = _adapterRegistry.Generic;
        var result = await adapter.ProbeAsync(candidate);

        if (!result.Reachable)
        {
            SetProbeState(InfoBarSeverity.Error,
                "Registration stopped",
                result.Detail ?? "The modem did not respond.");
            return;
        }

        if (!result.MatchesSelectedProfile)
        {
            SetProbeState(InfoBarSeverity.Warning,
                "Identity mismatch",
                $"The selected profile is {candidate.Manufacturer} {candidate.Model}, but the gateway appears to be {result.DetectedManufacturer ?? "an unknown vendor"} {result.DetectedModel ?? string.Empty}. Change the selection or use Auto detect before registering.");
            return;
        }

        var detectedManufacturer =
            result.DetectedManufacturer ??
            (candidate.Manufacturer == "Auto detect" ? "Generic" : candidate.Manufacturer);

        var detectedModel =
            result.DetectedModel ??
            (candidate.Model == "Auto detect" ? "Unknown" : candidate.Model);

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

        await _profileStore.SaveAsync(_profile);
        ApplyProfileToUi();

        SetProbeState(InfoBarSeverity.Success,
            _profile.IsLocked ? "Modem registered and locked" : "Modem registered",
            $"{_profile.Manufacturer} {_profile.Model} is now the active profile.");
    }

    private async void UnlockProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
            return;

        _profile.IsLocked = false;
        await _profileStore.SaveAsync(_profile);
        ApplyProfileToUi();

        SetProbeState(InfoBarSeverity.Informational,
            "Profile unlocked",
            "You can now select and register another modem.");
    }

    private void RefreshDashboard_Click(object sender, RoutedEventArgs e)
    {
        ApplyProfileToUi();
    }

    private ModemCandidate? TryBuildCandidate()
    {
        var brand = BrandComboBox.SelectedItem?.ToString() ?? "Auto detect";
        var model = ModelComboBox.SelectedItem?.ToString() ?? "Auto detect";

        var rawGateway = GatewayBox.Text.Trim();
        if (!rawGateway.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !rawGateway.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            rawGateway = "http://" + rawGateway;
        }

        if (!Uri.TryCreate(rawGateway, UriKind.Absolute, out var gateway))
        {
            SetProbeState(InfoBarSeverity.Error,
                "Invalid gateway",
                "Enter a valid modem address such as http://192.168.8.1.");
            return null;
        }

        return new ModemCandidate(brand, model, gateway);
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
            ProfileStateText.Text = "No modem has been registered yet.";
            LockBadgeText.Text = "Unlocked";
            LockBadgeIcon.Glyph = "";
            TitleProfileText.Text = "No modem registered";
            DashboardModemText.Text = "Not registered";
            DashboardGatewayText.Text = "Gateway —";

            ProfileInfoBar.IsOpen = true;
            ProfileInfoBar.Severity = InfoBarSeverity.Informational;
            ProfileInfoBar.Title = "No modem profile is locked";
            return;
        }

        ProfileStateText.Text =
            $"{_profile.DisplayName}  •  {_profile.Manufacturer} {_profile.Model}
{_profile.Gateway}";
        LockBadgeText.Text = locked ? "Locked" : "Unlocked";
        LockBadgeIcon.Glyph = locked ? "" : "";

        TitleProfileText.Text =
            $"{(locked ? "Locked" : "Registered")} • {_profile.Manufacturer} {_profile.Model}";
        DashboardModemText.Text = $"{_profile.Manufacturer} {_profile.Model}";
        DashboardGatewayText.Text = $"Gateway {_profile.Gateway}";

        ProfileNameBox.Text = _profile.DisplayName;
        GatewayBox.Text = _profile.Gateway;
        SelectComboValue(BrandComboBox, _profile.Manufacturer);
        SelectComboValue(ModelComboBox, _profile.Model);

        ProfileInfoBar.IsOpen = true;
        ProfileInfoBar.Severity = locked
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        ProfileInfoBar.Title = locked
            ? "Registered modem profile is locked"
            : "Registered modem profile is unlocked";
        ProfileInfoBar.Message = locked
            ? "Automatic adapter work will stay bound to this modem profile until you unlock it."
            : "You can change the modem selection and register a different profile.";
    }

    private static void SelectComboValue(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
        {
            if (item?.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true)
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

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(digest);
    }
}
