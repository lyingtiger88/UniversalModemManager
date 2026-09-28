using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UniversalModemManager.Core;
using UniversalModemManager.Models;

namespace UniversalModemManager.Adapters.Huawei;

public sealed class HuaweiHiLinkAdapter :
    IModemAdapter,
    IModemDashboardProvider,
    IModemNetworkProvider,
    IModemWifiProvider,
    IModemTrafficProvider,
    IModemConnectedDevicesProvider,
    IModemSmsProvider,
    IModemAuthenticationProvider,
    IDisposable
{
    private const string TokenHeader = "__RequestVerificationToken";

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly ConcurrentQueue<string> _postTokens = new();

    private CookieContainer? _cookies;
    private HttpClient? _http;
    private Uri? _baseUri;
    private string? _lastToken;
    private bool _initialized;

    public string Id => "huawei.hilink";
    public string DisplayName => "Huawei HiLink";

    public ModemCapability Capabilities =>
        ModemCapability.Identity |
        ModemCapability.Signal |
        ModemCapability.NetworkStatus |
        ModemCapability.Wifi |
        ModemCapability.WifiClients |
        ModemCapability.Traffic |
        ModemCapability.Sms |
        ModemCapability.Battery;

    public async Task<ModemProbeResult> ProbeAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureClient(candidate.Gateway);
            await EnsureSessionAsync(cancellationToken);

            var info = await TryGetXmlAsync("api/device/information", cancellationToken);
            var model = Value(info, "DeviceName", "devicename", "ModelName", "modelname");
            var firmware = Value(info, "SoftwareVersion", "softwareversion", "WebUIVersion");

            var manufacturerMatches =
                candidate.Manufacturer.Equals("Auto detect", StringComparison.OrdinalIgnoreCase) ||
                candidate.Manufacturer.Equals("Generic", StringComparison.OrdinalIgnoreCase) ||
                candidate.Manufacturer.Equals("Huawei", StringComparison.OrdinalIgnoreCase);

            var modelMatches = ModelMatches(candidate.Model, model);

            return new ModemProbeResult(
                Reachable: true,
                MatchesSelectedProfile: manufacturerMatches && modelMatches,
                DetectedManufacturer: "Huawei",
                DetectedModel: string.IsNullOrWhiteSpace(model) ? "HiLink modem" : model,
                Detail: string.IsNullOrWhiteSpace(firmware)
                    ? "Huawei HiLink local API detected."
                    : $"Huawei HiLink local API detected. Firmware {firmware}");
        }
        catch (Exception ex)
        {
            return new ModemProbeResult(
                Reachable: false,
                MatchesSelectedProfile: false,
                DetectedManufacturer: null,
                DetectedModel: null,
                Detail: ex.Message);
        }
    }

    public async Task<ModemIdentity?> GetIdentityAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var info = await TryGetXmlAsync("api/device/information", cancellationToken);
        if (info is null)
            return null;

        return new ModemIdentity(
            Manufacturer: "Huawei",
            Model: Value(info, "DeviceName", "devicename", "ModelName", "modelname") ?? "Huawei HiLink",
            Firmware: Value(info, "SoftwareVersion", "softwareversion", "WebUIVersion"),
            Gateway: _baseUri!.ToString(),
            AdapterId: Id,
            HardwareId: Value(info, "Imei", "IMEI", "SerialNumber", "Serial"));
    }

    public async Task<ModemDashboardSnapshot> GetDashboardAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var info = await TryGetXmlAsync("api/device/information", cancellationToken);
        var status = await TryGetXmlAsync("api/monitoring/status", cancellationToken);
        var signal = await TryGetXmlAsync("api/device/signal", cancellationToken);
        var wifi = await TryGetXmlAsync("api/wlan/basic-settings", cancellationToken);

        var signalIcon = ParseInt(Value(status, "SignalIcon"));
        var maxSignal = ParseInt(Value(status, "maxsignal")) ?? 5;
        int? signalPercent = null;

        if (signalIcon is not null && maxSignal > 0)
        {
            signalPercent = Math.Clamp(
                (int)Math.Round(signalIcon.Value * 100d / maxSignal),
                0,
                100);
        }

        var directSignal = ParseInt(Value(status, "SignalStrength"));
        if (directSignal is >= 0 and <= 100)
            signalPercent = directSignal;

        var clients = ParseInt(Value(status, "CurrentWifiUser", "currentwifiuser"));
        if (clients is null)
        {
            var hosts = await TryGetXmlAsync("api/wlan/host-list", cancellationToken);
            if (hosts is not null)
            {
                clients = hosts.Descendants()
                    .Count(x => x.Name.LocalName.Equals("Host", StringComparison.OrdinalIgnoreCase));
            }
        }

        var connectionCode = Value(status, "ConnectionStatus");
        var networkCode = Value(status, "CurrentNetworkTypeEx", "CurrentNetworkType");

        return new ModemDashboardSnapshot(
            Model: Value(info, "DeviceName", "devicename", "ModelName", "modelname")
                   ?? candidate.Model
                   ?? "Huawei HiLink",
            Firmware: Value(info, "SoftwareVersion", "softwareversion", "WebUIVersion"),
            ConnectionState: MapConnectionStatus(connectionCode),
            NetworkType: MapNetworkType(networkCode),
            WanIp: Value(status, "WanIPAddress", "WanIPv6Address"),
            SignalPercent: signalPercent,
            ConnectedClients: clients,
            BatteryPercent: ParseBatteryPercent(status),
            Ssid: Value(wifi, "WifiSsid", "SSID", "ssid"),
            Rsrp: Value(signal, "rsrp", "RSRP"),
            Rsrq: Value(signal, "rsrq", "RSRQ"),
            Sinr: Value(signal, "sinr", "SINR"));
    }

    public async Task<ModemNetworkSnapshot> GetNetworkAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var status = await TryGetXmlAsync("api/monitoring/status", cancellationToken);
        var signal = await TryGetXmlAsync("api/device/signal", cancellationToken);
        var plmn = await TryGetXmlAsync("api/net/current-plmn", cancellationToken);

        var signalIcon = ParseInt(Value(status, "SignalIcon"));
        var maxSignal = ParseInt(Value(status, "maxsignal")) ?? 5;
        int? signalPercent = null;

        if (signalIcon is not null && maxSignal > 0)
        {
            signalPercent = Math.Clamp(
                (int)Math.Round(signalIcon.Value * 100d / maxSignal),
                0,
                100);
        }

        var directSignal = ParseInt(Value(status, "SignalStrength"));
        if (directSignal is >= 0 and <= 100)
            signalPercent = directSignal;

        var roamingRaw = Value(status, "RoamingStatus", "roamingstatus");
        bool? roaming = roamingRaw switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        return new ModemNetworkSnapshot(
            ConnectionState: MapConnectionStatus(Value(status, "ConnectionStatus")),
            NetworkType: MapNetworkType(Value(status, "CurrentNetworkTypeEx", "CurrentNetworkType")),
            OperatorName: Value(plmn, "FullName", "ShortName", "Name"),
            OperatorCode: Value(plmn, "Numeric", "MccMnc", "PLMN"),
            WanIp: Value(status, "WanIPAddress", "WanIPv6Address"),
            SignalPercent: signalPercent,
            Rsrp: Value(signal, "rsrp", "RSRP"),
            Rsrq: Value(signal, "rsrq", "RSRQ"),
            Sinr: Value(signal, "sinr", "SINR"),
            BatteryPercent: ParseBatteryPercent(status),
            IsRoaming: roaming);
    }

    public async Task<ModemWifiSnapshot> GetWifiAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var xml = await GetXmlAsync(
            "api/wlan/basic-settings",
            cancellationToken,
            throwOnApiError: false);

        if (xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return new ModemWifiSnapshot(
                null, null, null, null, null, null, null);
        }

        return new ModemWifiSnapshot(
            Ssid: Value(xml, "WifiSsid", "SSID", "ssid"),
            Enabled: ParseBool01(Value(xml, "WifiEnable")),
            Hidden: ParseBool01(Value(xml, "WifiHide")),
            Channel: Value(xml, "WifiChannel"),
            Mode: Value(xml, "WifiMode"),
            MaxClients: ParseInt(Value(xml, "WifiMaxAssoc", "TotalWifiUser")),
            ClientIsolation: ParseBool01(Value(xml, "WifiIsolate")));
    }

    public async Task UpdateWifiAsync(
        ModemCandidate candidate,
        WifiUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var current = await GetXmlAsync(
            "api/wlan/basic-settings",
            cancellationToken);

        if (request.Ssid is not null &&
            string.IsNullOrWhiteSpace(request.Ssid))
        {
            throw new ArgumentException(
                "SSID cannot be empty.",
                nameof(request));
        }

        if (request.MaxClients is <= 0)
        {
            throw new ArgumentException(
                "Max clients must be greater than zero.",
                nameof(request));
        }

        var payload = new XElement("request");

        AddWifiSetting(
            payload,
            "WifiEnable",
            request.Enabled is null
                ? Value(current, "WifiEnable")
                : request.Enabled.Value ? "1" : "0");

        AddWifiSetting(
            payload,
            "WifiSsid",
            request.Ssid?.Trim() ??
            Value(current, "WifiSsid", "SSID", "ssid"));

        AddWifiSetting(
            payload,
            "WifiHide",
            request.Hidden is null
                ? Value(current, "WifiHide")
                : request.Hidden.Value ? "1" : "0");

        AddWifiSetting(
            payload,
            "WifiCountry",
            Value(current, "WifiCountry"));

        AddWifiSetting(
            payload,
            "WifiChannel",
            request.Channel?.Trim() ??
            Value(current, "WifiChannel"));

        AddWifiSetting(
            payload,
            "WifiMode",
            Value(current, "WifiMode"));

        AddWifiSetting(
            payload,
            "WifiRate",
            Value(current, "WifiRate"));

        AddWifiSetting(
            payload,
            "WifiTxPower",
            Value(current, "WifiTxPower"));

        AddWifiSetting(
            payload,
            "WifiMaxAssoc",
            request.MaxClients?.ToString() ??
            Value(current, "WifiMaxAssoc", "TotalWifiUser"));

        AddWifiSetting(
            payload,
            "WifiIsolate",
            request.ClientIsolation is null
                ? Value(current, "WifiIsolate")
                : request.ClientIsolation.Value ? "1" : "0");

        AddWifiSetting(
            payload,
            "WifiWMM",
            Value(current, "WifiWMM"));

        payload.Add(new XElement("WifiRestart", "1"));

        var result = await PostXmlAsync(
            "api/wlan/basic-settings",
            payload,
            cancellationToken);

        if (!IsOkResponse(result))
        {
            throw new HuaweiHiLinkException(
                "The modem did not confirm the Wi-Fi settings change.");
        }
    }

    public async Task<TrafficStatistics> GetTrafficAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var xml = await GetXmlAsync(
            "api/monitoring/traffic-statistics",
            cancellationToken);

        return new TrafficStatistics(
            CurrentConnectTimeSeconds:
                ParseLong(Value(xml, "CurrentConnectTime")),
            CurrentUploadBytes:
                ParseLong(Value(xml, "CurrentUpload")),
            CurrentDownloadBytes:
                ParseLong(Value(xml, "CurrentDownload")),
            CurrentUploadRateBytesPerSecond:
                ParseLong(Value(xml, "CurrentUploadRate")),
            CurrentDownloadRateBytesPerSecond:
                ParseLong(Value(xml, "CurrentDownloadRate")),
            TotalUploadBytes:
                ParseLong(Value(xml, "TotalUpload")),
            TotalDownloadBytes:
                ParseLong(Value(xml, "TotalDownload")),
            TotalConnectTimeSeconds:
                ParseLong(Value(xml, "TotalConnectTime")));
    }

    public async Task<MonthTrafficStatistics> GetMonthTrafficAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var xml = await GetXmlAsync(
            "api/monitoring/month_statistics",
            cancellationToken,
            throwOnApiError: false);

        if (xml.Name.LocalName.Equals(
                "error",
                StringComparison.OrdinalIgnoreCase))
        {
            return new MonthTrafficStatistics(
                null,
                null,
                null);
        }

        return new MonthTrafficStatistics(
            UploadBytes:
                ParseLong(Value(
                    xml,
                    "CurrentMonthUpload",
                    "MonthUpload")),
            DownloadBytes:
                ParseLong(Value(
                    xml,
                    "CurrentMonthDownload",
                    "MonthDownload")),
            DurationSeconds:
                ParseLong(Value(
                    xml,
                    "MonthDuration",
                    "CurrentMonthDuration")));
    }

    public async Task<IReadOnlyList<ConnectedDevice>>
        GetConnectedDevicesAsync(
            ModemCandidate candidate,
            CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var xml = await GetXmlAsync(
            "api/wlan/host-list",
            cancellationToken,
            throwOnApiError: false);

        var result = new List<ConnectedDevice>();

        if (!xml.Name.LocalName.Equals(
                "error",
                StringComparison.OrdinalIgnoreCase))
        {
            AddClientNodes(
                xml,
                result,
                "Host");
        }

        if (result.Count == 0)
        {
            try
            {
                var stations = await GetXmlAsync(
                    "api/wlan/station-information",
                    cancellationToken,
                    throwOnApiError: false);

                if (!stations.Name.LocalName.Equals(
                        "error",
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddClientNodes(stations, result, "Host");
                    AddClientNodes(stations, result, "Station");
                    AddClientNodes(stations, result, "WifiHost");
                }
            }
            catch
            {
                // Optional fallback endpoint.
            }
        }

        var clients = result
            .Where(x =>
                x.IpAddress != "-" ||
                x.MacAddress != "-")
            .GroupBy(
                x => x.MacAddress != "-"
                    ? x.MacAddress
                    : x.IpAddress,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        try
        {
            clients = await EnrichClientTrafficAsync(
                clients,
                cancellationToken);
        }
        catch
        {
            // Per-client counters are optional on Huawei firmware.
        }

        return clients;
    }

    private async Task<List<ConnectedDevice>>
        EnrichClientTrafficAsync(
            List<ConnectedDevice> clients,
            CancellationToken cancellationToken)
    {
        var xml = await GetXmlAsync(
            "api/monitoring/lan-host-detail",
            cancellationToken,
            throwOnApiError: false);

        if (xml.Name.LocalName.Equals(
                "error",
                StringComparison.OrdinalIgnoreCase))
        {
            return clients;
        }

        var enriched =
            new List<ConnectedDevice>(clients.Count);

        foreach (var client in clients)
        {
            var match =
                xml.DescendantsAndSelf()
                    .FirstOrDefault(node =>
                    {
                        var mac = Value(
                            node,
                            "MacAddress",
                            "MACAddress",
                            "macaddress",
                            "Mac",
                            "MAC");

                        return !string.IsNullOrWhiteSpace(mac) &&
                               MacEquals(
                                   mac,
                                   client.MacAddress);
                    });

            if (match is null)
            {
                enriched.Add(client);
                continue;
            }

            var upload = ParseLong(
                Value(
                    match,
                    "UploadBytes",
                    "UpBytes",
                    "CurrentUpload",
                    "TotalUpload",
                    "Upload"));

            var download = ParseLong(
                Value(
                    match,
                    "DownloadBytes",
                    "DownBytes",
                    "CurrentDownload",
                    "TotalDownload",
                    "Download"));

            enriched.Add(
                client with
                {
                    UploadBytes =
                        upload ?? client.UploadBytes,
                    DownloadBytes =
                        download ?? client.DownloadBytes
                });
        }

        return enriched;
    }

    public async Task<SmsCounts> GetSmsCountsAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var xml = await GetXmlAsync("api/sms/sms-count", cancellationToken);

        return new SmsCounts(
            LocalUnread: ParseInt(Value(xml, "LocalUnread")) ?? 0,
            LocalInbox: ParseInt(Value(xml, "LocalInbox")) ?? 0,
            LocalOutbox: ParseInt(Value(xml, "LocalOutbox")) ?? 0,
            LocalDraft: ParseInt(Value(xml, "LocalDraft")) ?? 0,
            SimUnread: ParseInt(Value(xml, "SimUnread")) ?? 0,
            SimInbox: ParseInt(Value(xml, "SimInbox")) ?? 0);
    }

    public async Task<IReadOnlyList<SmsMessage>> GetSmsMessagesAsync(
        ModemCandidate candidate,
        SmsBoxType box,
        int page = 1,
        int readCount = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        if (page < 1)
            page = 1;

        readCount = Math.Clamp(readCount, 1, 20);

        var payload = new XElement(
            "request",
            new XElement("PageIndex", page),
            new XElement("ReadCount", readCount),
            new XElement("BoxType", (int)box),
            new XElement("SortType", 0),
            new XElement("Ascending", 0),
            new XElement("UnreadPreferred", 0));

        var xml = await PostXmlAsync(
            "api/sms/sms-list",
            payload,
            cancellationToken);

        return xml.Descendants()
            .Where(x => x.Name.LocalName.Equals(
                "Message",
                StringComparison.OrdinalIgnoreCase))
            .Select(message => new SmsMessage(
                Index: Value(message, "Index") ?? string.Empty,
                Phone: Value(message, "Phone") ?? "Unknown",
                Content: Value(message, "Content") ?? string.Empty,
                Date: Value(message, "Date") ?? string.Empty,
                IsRead: Value(message, "Smstat", "Read") == "1",
                SmsType: Value(message, "SmsType"),
                Sca: Value(message, "Sca")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Index))
            .ToList();
    }

    public async Task SendSmsAsync(
        ModemCandidate candidate,
        string phone,
        string message,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException(
                "A destination phone number is required.",
                nameof(phone));

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException(
                "The SMS message is empty.",
                nameof(message));

        var payload = new XElement(
            "request",
            new XElement("Index", -1),
            new XElement("Phones",
                new XElement("Phone", phone.Trim())),
            new XElement("Sca", string.Empty),
            new XElement("Content", message),
            new XElement("Length", message.Length),
            new XElement("Reserved", 1),
            new XElement(
                "Date",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

        var result = await PostXmlAsync(
            "api/sms/send-sms",
            payload,
            cancellationToken);

        if (!IsOkResponse(result))
            throw new HuaweiHiLinkException(
                "The modem did not confirm SMS submission.");
    }

    public async Task MarkSmsReadAsync(
        ModemCandidate candidate,
        string index,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);

        var result = await PostXmlAsync(
            "api/sms/set-read",
            new XElement(
                "request",
                new XElement("Index", index)),
            cancellationToken);

        if (!IsOkResponse(result))
            throw new HuaweiHiLinkException(
                "The modem did not confirm the read-state change.");
    }

    public async Task DeleteSmsAsync(
        ModemCandidate candidate,
        string index,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);

        var result = await PostXmlAsync(
            "api/sms/delete-sms",
            new XElement(
                "request",
                new XElement("Index", index)),
            cancellationToken);

        if (!IsOkResponse(result))
            throw new HuaweiHiLinkException(
                "The modem did not confirm SMS deletion.");
    }

    public async Task<ModemAuthenticationState> GetAuthenticationStateAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);
        await EnsureSessionAsync(cancellationToken);

        var xml = await GetXmlAsync(
            "api/user/state-login",
            cancellationToken,
            throwOnApiError: false);

        if (xml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            var code = ErrorCode(xml);
            return new ModemAuthenticationState(
                Supported: false,
                IsLoggedIn: false,
                IsLocked: false,
                RemainingWaitSeconds: 0,
                Detail: $"Huawei login-state API returned {code ?? "an error"}.");
        }

        var state = ParseHuaweiLoginState(xml);
        return new ModemAuthenticationState(
            Supported: true,
            IsLoggedIn: state.IsLoggedIn,
            IsLocked: state.IsLocked,
            RemainingWaitSeconds: state.RemainingWaitSeconds,
            Detail: state.IsLoggedIn
                ? "Authenticated HiLink session."
                : "Huawei HiLink admin authentication is available.");
    }

    public async Task<ModemLoginResult> LoginAsync(
        ModemCandidate candidate,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);

        await _authGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSessionAsync(cancellationToken);

            var stateXml = await GetXmlAsync(
                "api/user/state-login",
                cancellationToken,
                throwOnApiError: false);

            if (stateXml.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                return new ModemLoginResult(
                    false,
                    "This Huawei firmware did not expose the HiLink login-state API.",
                    new ModemAuthenticationState(false, false, false, 0));
            }

            var state = ParseHuaweiLoginState(stateXml);

            if (state.IsLoggedIn)
            {
                return new ModemLoginResult(
                    true,
                    "Already logged in.",
                    new ModemAuthenticationState(true, true, false, 0));
            }

            if (state.IsLocked)
            {
                var message = state.RemainingWaitSeconds > 0
                    ? $"Login is temporarily locked by the modem. Wait about {state.RemainingWaitSeconds} second(s)."
                    : "Login is temporarily locked by the modem after failed attempts.";

                return new ModemLoginResult(
                    false,
                    message,
                    new ModemAuthenticationState(
                        true,
                        false,
                        true,
                        state.RemainingWaitSeconds,
                        message));
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                return new ModemLoginResult(
                    false,
                    "Enter the modem admin password.",
                    new ModemAuthenticationState(true, false, false, 0));
            }

            username = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();

            var passwordType = state.PasswordType;
            if (passwordType != "3" && passwordType != "4")
                passwordType = "4";

            var token = await TakePostTokenAsync(cancellationToken);

            var encodedPassword = passwordType == "3"
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(password))
                : Base64Sha256Hex(username + Base64Sha256Hex(password) + token);

            var payload = new XElement(
                "request",
                new XElement("Username", username),
                new XElement("Password", encodedPassword),
                new XElement("password_type", passwordType));

            try
            {
                var result = await PostXmlAsync(
                    "api/user/login",
                    payload,
                    cancellationToken,
                    token);

                var ok = IsOkResponse(result);

                return new ModemLoginResult(
                    ok,
                    ok ? "Huawei admin login succeeded." : "The modem did not accept the login.",
                    new ModemAuthenticationState(true, ok, false, 0));
            }
            catch (HuaweiHiLinkException ex)
            {
                var after = await SafeAuthenticationStateAsync(candidate, cancellationToken);
                return new ModemLoginResult(false, ex.Message, after);
            }
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<ModemAuthenticationState> SafeAuthenticationStateAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetAuthenticationStateAsync(candidate, cancellationToken);
        }
        catch
        {
            return new ModemAuthenticationState(true, false, false, 0);
        }
    }

    private void EnsureClient(Uri gateway)
    {
        var normalized = NormalizeBaseUri(gateway);

        if (_baseUri is not null &&
            Uri.Compare(
                _baseUri,
                normalized,
                UriComponents.SchemeAndServer,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0 &&
            _http is not null)
        {
            return;
        }

        _http?.Dispose();

        _baseUri = normalized;
        _cookies = new CookieContainer();
        _postTokens.Clear();
        _lastToken = null;
        _initialized = false;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(8)
        };

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/xml"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Requested-With",
            "XMLHttpRequest");
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            if (_http is null || _baseUri is null || _cookies is null)
                throw new InvalidOperationException("Huawei HTTP client is not initialized.");

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "api/webserver/SesTokInfo");

            using var response = await _http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            var xml = ParseXml(text);
            ThrowIfApiError(xml, "api/webserver/SesTokInfo");

            var sesInfo = Value(xml, "SesInfo");
            var tokInfo = Value(xml, "TokInfo");

            if (string.IsNullOrWhiteSpace(sesInfo) ||
                string.IsNullOrWhiteSpace(tokInfo))
            {
                throw new HuaweiHiLinkException(
                    "The device responded, but it did not return a Huawei HiLink session/token.");
            }

            var sessionId = sesInfo.StartsWith(
                "SessionID=",
                StringComparison.OrdinalIgnoreCase)
                    ? sesInfo["SessionID=".Length..]
                    : sesInfo;

            _cookies.SetCookies(_baseUri, $"SessionID={sessionId}");
            SetTokens(new[] { tokInfo });
            CaptureTokens(response, append: true);
            _initialized = true;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<XElement?> TryGetXmlAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var xml = await GetXmlAsync(
                path,
                cancellationToken,
                throwOnApiError: false);

            return xml.Name.LocalName.Equals(
                "error",
                StringComparison.OrdinalIgnoreCase)
                    ? null
                    : xml;
        }
        catch
        {
            return null;
        }
    }

    private async Task<XElement> GetXmlAsync(
        string path,
        CancellationToken cancellationToken,
        bool throwOnApiError = true)
    {
        if (_http is null)
            throw new InvalidOperationException("Huawei HTTP client is not initialized.");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        response.EnsureSuccessStatusCode();
        CaptureTokens(response);

        var xml = ParseXml(text);
        if (throwOnApiError)
            ThrowIfApiError(xml, path);

        return xml;
    }

    private async Task<XElement> PostXmlAsync(
        string path,
        XElement payload,
        CancellationToken cancellationToken,
        string? explicitToken = null)
    {
        await EnsureSessionAsync(cancellationToken);

        if (_http is null)
            throw new InvalidOperationException("Huawei HTTP client is not initialized.");

        var token = explicitToken ?? await TakePostTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                payload.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8,
                "application/x-www-form-urlencoded")
        };

        request.Headers.TryAddWithoutValidation(TokenHeader, token);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        response.EnsureSuccessStatusCode();
        CaptureTokens(response);

        var xml = ParseXml(text);
        ThrowIfApiError(xml, path);
        return xml;
    }

    private Task<string> TakePostTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_postTokens.TryDequeue(out var queued) &&
            !string.IsNullOrWhiteSpace(queued))
        {
            _lastToken = queued;
            return Task.FromResult(queued);
        }

        if (!string.IsNullOrWhiteSpace(_lastToken))
            return Task.FromResult(_lastToken);

        throw new HuaweiHiLinkException(
            "No Huawei request verification token is available.");
    }

    private void CaptureTokens(
        HttpResponseMessage response,
        bool append = false)
    {
        var ordered = new List<string>();

        AddHeaderTokens(response, TokenHeader + "one", ordered);
        AddHeaderTokens(response, TokenHeader + "two", ordered);
        AddHeaderTokens(response, TokenHeader, ordered);

        if (ordered.Count == 0)
            return;

        if (!append)
            _postTokens.Clear();

        foreach (var token in ordered)
            _postTokens.Enqueue(token);

        _lastToken = ordered[^1];
    }

    private static void AddHeaderTokens(
        HttpResponseMessage response,
        string headerName,
        List<string> output)
    {
        if (!response.Headers.TryGetValues(headerName, out var values))
            return;

        foreach (var raw in values)
        {
            foreach (var token in raw.Split(
                         '#',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(token))
                    output.Add(token);
            }
        }
    }

    private void SetTokens(IEnumerable<string> tokens)
    {
        _postTokens.Clear();

        foreach (var token in tokens.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            _postTokens.Enqueue(token);
            _lastToken = token;
        }
    }

    private static HuaweiLoginState ParseHuaweiLoginState(XElement xml)
    {
        var rawState = Value(xml, "State", "state");
        var loggedIn = rawState == "0";

        var remaining = ParseInt(
            Value(xml, "remainwaittime", "RemainWaitTime")) ?? 0;

        var lockStatus = Value(xml, "lockstatus", "LockStatus");
        var locked = remaining > 0 || lockStatus == "1";

        var passwordType =
            Value(xml, "password_type", "PasswordType") ?? "4";

        return new HuaweiLoginState(
            loggedIn,
            locked,
            Math.Max(0, remaining),
            passwordType);
    }

    private static bool ModelMatches(
        string selectedModel,
        string? detectedModel)
    {
        if (selectedModel.Equals("Auto detect", StringComparison.OrdinalIgnoreCase) ||
            selectedModel.Equals("Other / Unknown", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(detectedModel))
        {
            return true;
        }

        if (selectedModel.Contains("E5573", StringComparison.OrdinalIgnoreCase) &&
            detectedModel.Contains("E5573", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return detectedModel.Contains(
            selectedModel,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseBatteryPercent(XElement? status)
    {
        var percent = ParseInt(Value(status, "BatteryPercent"));
        if (percent is >= 0 and <= 100)
            return percent;

        var level = ParseInt(Value(status, "BatteryLevel"));
        if (level is >= 0 and <= 4)
            return level * 25;

        return null;
    }

    private static string Base64Sha256Hex(string input)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(hex));
    }

    private static string MapConnectionStatus(string? code) => code switch
    {
        "1" => "Roaming / Connected",
        "900" => "Connecting",
        "901" => "Connected",
        "902" => "Disconnected",
        "903" => "Disconnecting",
        "904" => "Connection failed",
        "905" => "No service",
        _ => string.IsNullOrWhiteSpace(code) ? "Unknown" : $"Unknown ({code})"
    };

    private static string MapNetworkType(string? code) => code switch
    {
        "1" => "GSM",
        "2" => "GPRS",
        "3" => "EDGE",
        "4" => "WCDMA / 3G",
        "5" => "HSDPA",
        "6" => "HSUPA",
        "7" => "HSPA",
        "9" => "HSPA+",
        "19" => "LTE / 4G",
        "101" => "LTE / 4G",
        _ => string.IsNullOrWhiteSpace(code) ? "Unknown" : $"Network code {code}"
    };

    private static XElement ParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text).Root
                   ?? throw new HuaweiHiLinkException("Empty XML response.");
        }
        catch (Exception ex) when (ex is not HuaweiHiLinkException)
        {
            throw new HuaweiHiLinkException(
                "Invalid XML returned by the device.",
                ex);
        }
    }

    private static string? Value(
        XElement? parent,
        params string[] names)
    {
        if (parent is null)
            return null;

        foreach (var name in names)
        {
            var node = parent
                .DescendantsAndSelf()
                .FirstOrDefault(x => x.Name.LocalName.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (node is not null &&
                !string.IsNullOrWhiteSpace(node.Value))
            {
                return node.Value.Trim();
            }
        }

        return null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var result) ? result : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, out var result) ? result : null;

    private static bool? ParseBool01(string? value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => null
    };

    private static void AddWifiSetting(
        XElement payload,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            payload.Add(new XElement(name, value));
    }

    private static void AddClientNodes(
        XElement xml,
        List<ConnectedDevice> output,
        string nodeName)
    {
        foreach (var node in
                 xml.Descendants()
                    .Where(x =>
                        x.Name.LocalName.Equals(
                            nodeName,
                            StringComparison.OrdinalIgnoreCase)))
        {
            var ip = Value(
                node,
                "IpAddress",
                "IPAddress",
                "ipaddress",
                "IP");

            var mac = Value(
                node,
                "MacAddress",
                "MACAddress",
                "macaddress",
                "Mac",
                "MAC");

            if (string.IsNullOrWhiteSpace(ip) &&
                string.IsNullOrWhiteSpace(mac))
            {
                continue;
            }

            output.Add(
                new ConnectedDevice(
                    HostName:
                        Value(
                            node,
                            "HostName",
                            "hostname",
                            "Name",
                            "DeviceName")
                        ?? "Unknown device",
                    IpAddress:
                        ip ?? "-",
                    MacAddress:
                        NormalizeMac(mac) ?? mac ?? "-",
                    AssociatedTimeSeconds:
                        ParseLong(
                            Value(
                                node,
                                "AssociatedTime",
                                "associatedtime",
                                "ConnectTime")),
                    UploadBytes:
                        ParseLong(
                            Value(
                                node,
                                "UploadBytes",
                                "UpBytes",
                                "CurrentUpload",
                                "TotalUpload",
                                "Upload")),
                    DownloadBytes:
                        ParseLong(
                            Value(
                                node,
                                "DownloadBytes",
                                "DownBytes",
                                "CurrentDownload",
                                "TotalDownload",
                                "Download"))));
        }
    }

    private static string? NormalizeMac(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var hex = new string(
            value.Where(Uri.IsHexDigit).ToArray())
            .ToUpperInvariant();

        if (hex.Length != 12)
            return null;

        return string.Join(
            ":",
            Enumerable.Range(0, 6)
                .Select(i =>
                    hex.Substring(i * 2, 2)));
    }

    private static bool MacEquals(
        string? left,
        string? right)
    {
        var a = NormalizeMac(left);
        var b = NormalizeMac(right);

        return a is not null &&
               b is not null &&
               a.Equals(
                   b,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? ErrorCode(XElement xml) =>
        Value(xml, "code", "Code");

    private static void ThrowIfApiError(
        XElement xml,
        string? endpoint = null)
    {
        if (!xml.Name.LocalName.Equals(
                "error",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var code = ErrorCode(xml) ?? "unknown";
        var message = code switch
        {
            "100002" => "This Huawei firmware does not expose the requested API.",
            "100003" => "The Huawei modem is busy.",
            "100005" => "The Huawei modem denied the request.",
            "100006" => "The Huawei modem rejected one or more request parameters.",
            "108001" => "Username or password is incorrect.",
            "108002" => "Username or password is incorrect.",
            "108003" => "The admin user is already logged in.",
            "108006" => "Huawei admin login is required.",
            "108007" => "Huawei admin login is temporarily locked after failed attempts.",
            "113017" => "The SMS request contains an invalid or unsupported argument.",
            "113018" => "The SMS operation timed out.",
            "113020" => "The modem could not query the requested SMS list.",
            "113036" => "The modem could not delete the SMS.",
            "113053" => "SMS storage does not have enough free space.",
            "113054" => "The destination phone number is too long.",
            "125001" => "Invalid Huawei request verification token.",
            "125002" => "Invalid Huawei request verification token.",
            "125003" => "Invalid Huawei session.",
            _ => "Huawei HiLink API error."
        };

        var where = string.IsNullOrWhiteSpace(endpoint)
            ? string.Empty
            : $", endpoint {endpoint}";

        throw new HuaweiHiLinkException(
            $"{message} (code {code}{where})",
            code);
    }

    private static bool IsOkResponse(XElement xml) =>
        xml.Name.LocalName.Equals(
            "response",
            StringComparison.OrdinalIgnoreCase) &&
        xml.Value.Trim().Equals(
            "OK",
            StringComparison.OrdinalIgnoreCase);

    private static Uri NormalizeBaseUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    public void Dispose()
    {
        _http?.Dispose();
        _sessionGate.Dispose();
        _authGate.Dispose();
    }

    private sealed record HuaweiLoginState(
        bool IsLoggedIn,
        bool IsLocked,
        int RemainingWaitSeconds,
        string PasswordType);
}

public sealed class HuaweiHiLinkException : Exception
{
    public string? ApiCode { get; }

    public HuaweiHiLinkException(
        string message,
        string? apiCode = null)
        : base(message)
    {
        ApiCode = apiCode;
    }

    public HuaweiHiLinkException(
        string message,
        Exception inner)
        : base(message, inner)
    {
    }
}
