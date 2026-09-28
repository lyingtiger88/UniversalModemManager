using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UniversalModemManager.Core;
using UniversalModemManager.Models;

namespace UniversalModemManager.Adapters.Huawei;

public sealed class HuaweiHg532dAdapter :
    IModemAdapter,
    IModemAuthenticationProvider,
    IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CookieContainer? _cookies;
    private HttpClient? _http;
    private Uri? _baseUri;

    public string Id => "huawei.hg532d";
    public string DisplayName => "Huawei HG532d";

    // Do not claim Wi-Fi/network editing until the legacy ASP/CGI pages have
    // been verified against the user's firmware.
    public ModemCapability Capabilities =>
        ModemCapability.Identity;

    public async Task<ModemProbeResult> ProbeAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (!CandidateCanBeHg532d(candidate))
        {
            return new ModemProbeResult(
                Reachable: false,
                MatchesSelectedProfile: false,
                DetectedManufacturer: null,
                DetectedModel: null,
                Detail: "The selected profile is not an HG532d.");
        }

        try
        {
            EnsureClient(candidate.Gateway);

            var page = await GetTextAsync(
                "/",
                cancellationToken,
                allowFailureStatus: true);

            var looksHuawei =
                page.Contains(
                    "Huawei",
                    StringComparison.OrdinalIgnoreCase) ||
                page.Contains(
                    "Home Gateway",
                    StringComparison.OrdinalIgnoreCase) ||
                page.Contains(
                    "HG532",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Model.Equals(
                    "HG532d",
                    StringComparison.OrdinalIgnoreCase);

            if (!looksHuawei)
            {
                return new ModemProbeResult(
                    Reachable: true,
                    MatchesSelectedProfile: false,
                    DetectedManufacturer: null,
                    DetectedModel: null,
                    Detail: "A web interface responded, but it did not look like Huawei HG532d.");
            }

            return new ModemProbeResult(
                Reachable: true,
                MatchesSelectedProfile: true,
                DetectedManufacturer: "Huawei",
                DetectedModel: "HG532d",
                Detail: "Huawei HG532d legacy web interface detected.");
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

    public Task<ModemIdentity?> GetIdentityAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ModemIdentity identity = new(
            Manufacturer: "Huawei",
            Model: "HG532d",
            Firmware: null,
            Gateway: NormalizeBaseUri(candidate.Gateway).ToString(),
            AdapterId: Id);

        return Task.FromResult<ModemIdentity?>(identity);
    }

    public async Task<ModemAuthenticationState> GetAuthenticationStateAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = await GetTextAsync(
                "/",
                cancellationToken,
                allowFailureStatus: true);

            var loginVisible = LooksLikeLoginPage(page);

            return new ModemAuthenticationState(
                Supported: true,
                IsLoggedIn: !loginVisible,
                IsLocked: false,
                RemainingWaitSeconds: 0,
                Detail: loginVisible
                    ? "HG532d web login is available."
                    : "HG532d web session appears authenticated.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModemLoginResult> LoginAsync(
        ModemCandidate candidate,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        EnsureClient(candidate.Gateway);

        if (string.IsNullOrWhiteSpace(username))
        {
            return Failure(
                "Enter the HG532d web-management username.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return Failure(
                "Enter the HG532d web-management password.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var loginPage = await GetTextAsync(
                "/",
                cancellationToken,
                allowFailureStatus: true);

            if (!LooksLikeLoginPage(loginPage))
            {
                return new ModemLoginResult(
                    true,
                    "The HG532d session is already authenticated.",
                    new ModemAuthenticationState(
                        true,
                        true,
                        false,
                        0,
                        "Authenticated HG532d web session."));
            }

            var action =
                DetectLoginAction(loginPage) ??
                "/index/login.cgi";

            var encodedPassword =
                PageUsesHashedPassword(loginPage)
                    ? EncodeLegacyHuaweiPassword(password)
                    : password;

            var fields =
                ExtractHiddenFields(loginPage);

            fields["Username"] = username.Trim();
            fields["Password"] = encodedPassword;

            // Some HG532 firmware expects this cookie state before accepting
            // the legacy login form.
            SetCookie("Language", "en");
            SetCookie("FirstMenu", "Admin_0");
            SetCookie("SecondMenu", "Admin_0_0");
            SetCookie("ThirdMenu", "Admin_0_0_0");

            using var content =
                new FormUrlEncodedContent(fields);

            using var response =
                await _http!.PostAsync(
                    action,
                    content,
                    cancellationToken);

            var responseBody =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            // Follow up with the root page because several HG532 builds return
            // HTTP 200 even when the submitted credentials are wrong.
            var verifyPage =
                await GetTextAsync(
                    "/",
                    cancellationToken,
                    allowFailureStatus: true);

            var authenticated =
                !LooksLikeLoginPage(verifyPage) &&
                !LooksLikeLoginFailure(responseBody);

            if (authenticated)
            {
                return new ModemLoginResult(
                    true,
                    "Huawei HG532d web login succeeded.",
                    new ModemAuthenticationState(
                        true,
                        true,
                        false,
                        0,
                        "Authenticated HG532d web session."));
            }

            return Failure(
                "The HG532d did not accept the supplied username/password. This firmware may use different credentials or a different login encoding.");
        }
        catch (Exception ex)
        {
            return Failure(
                $"HG532d login failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureClient(
        Uri gateway)
    {
        var normalized =
            NormalizeBaseUri(gateway);

        if (_http is not null &&
            _baseUri is not null &&
            Uri.Compare(
                _baseUri,
                normalized,
                UriComponents.SchemeAndServer,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            return;
        }

        _http?.Dispose();

        _baseUri = normalized;
        _cookies = new CookieContainer();

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(8)
        };

        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "UniversalModemManager",
                "0.3"));

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "text/html"));
    }

    private async Task<string> GetTextAsync(
        string path,
        CancellationToken cancellationToken,
        bool allowFailureStatus)
    {
        if (_http is null)
            throw new InvalidOperationException(
                "HG532d HTTP client is not initialized.");

        using var response =
            await _http.GetAsync(
                path,
                cancellationToken);

        if (!allowFailureStatus)
            response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(
            cancellationToken);
    }

    private void SetCookie(
        string name,
        string value)
    {
        if (_cookies is null ||
            _baseUri is null)
        {
            return;
        }

        _cookies.Add(
            _baseUri,
            new Cookie(
                name,
                value,
                "/"));
    }

    private static bool CandidateCanBeHg532d(
        ModemCandidate candidate) =>
        candidate.Model.Equals(
            "HG532d",
            StringComparison.OrdinalIgnoreCase) ||
        (
            candidate.Manufacturer.Equals(
                "Huawei",
                StringComparison.OrdinalIgnoreCase) &&
            candidate.Model.Contains(
                "HG532",
                StringComparison.OrdinalIgnoreCase)
        );

    private static bool LooksLikeLoginPage(
        string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var hasPassword =
            Regex.IsMatch(
                html,
                @"type\s*=\s*['\"]?password",
                RegexOptions.IgnoreCase);

        var hasLoginAction =
            html.Contains(
                "/index/login.cgi",
                StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                html,
                @"action\s*=\s*['\"][^'\"]*login",
                RegexOptions.IgnoreCase);

        var hasCredentialNames =
            html.Contains(
                "Username",
                StringComparison.OrdinalIgnoreCase) &&
            html.Contains(
                "Password",
                StringComparison.OrdinalIgnoreCase);

        return hasPassword &&
               (hasLoginAction || hasCredentialNames);
    }

    private static bool LooksLikeLoginFailure(
        string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return false;

        string[] markers =
        [
            "incorrect",
            "invalid password",
            "wrong password",
            "login failed",
            "username or password",
            "authentication failed"
        ];

        return markers.Any(marker =>
            html.Contains(
                marker,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? DetectLoginAction(
        string html)
    {
        var match =
            Regex.Match(
                html,
                @"<form[^>]*action\s*=\s*['\"](?<action>[^'\"]+)['\"]",
                RegexOptions.IgnoreCase |
                RegexOptions.Singleline);

        if (!match.Success)
            return null;

        var action =
            WebUtility.HtmlDecode(
                match.Groups["action"].Value.Trim());

        if (string.IsNullOrWhiteSpace(action))
            return null;

        if (!action.StartsWith(
                "/",
                StringComparison.Ordinal))
        {
            action = "/" + action.TrimStart('/');
        }

        return action;
    }

    private static Dictionary<string, string>
        ExtractHiddenFields(
            string html)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var matches =
            Regex.Matches(
                html,
                @"<input\b[^>]*type\s*=\s*['\"]?hidden['\"]?[^>]*>",
                RegexOptions.IgnoreCase |
                RegexOptions.Singleline);

        foreach (Match input in matches)
        {
            var name =
                ExtractHtmlAttribute(
                    input.Value,
                    "name");

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var value =
                ExtractHtmlAttribute(
                    input.Value,
                    "value") ??
                string.Empty;

            result[name] = value;
        }

        return result;
    }

    private static string? ExtractHtmlAttribute(
        string html,
        string attribute)
    {
        var match =
            Regex.Match(
                html,
                $@"\b{Regex.Escape(attribute)}\s*=\s*['\"](?<value>[^'\"]*)['\"]",
                RegexOptions.IgnoreCase);

        return match.Success
            ? WebUtility.HtmlDecode(
                match.Groups["value"].Value)
            : null;
    }

    private static bool PageUsesHashedPassword(
        string html) =>
        html.Contains(
            "sha256",
            StringComparison.OrdinalIgnoreCase) ||
        html.Contains(
            "SHA256",
            StringComparison.OrdinalIgnoreCase) ||
        (
            html.Contains(
                "base64",
                StringComparison.OrdinalIgnoreCase) &&
            html.Contains(
                "Password",
                StringComparison.OrdinalIgnoreCase)
        );

    private static string EncodeLegacyHuaweiPassword(
        string password)
    {
        var digest =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(password));

        var hex =
            Convert.ToHexString(digest)
                .ToLowerInvariant();

        return Convert.ToBase64String(
            Encoding.ASCII.GetBytes(hex));
    }

    private static ModemLoginResult Failure(
        string message) =>
        new(
            false,
            message,
            new ModemAuthenticationState(
                true,
                false,
                false,
                0,
                message));

    private static Uri NormalizeBaseUri(
        Uri uri)
    {
        var builder =
            new UriBuilder(uri)
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
        _gate.Dispose();
    }
}
