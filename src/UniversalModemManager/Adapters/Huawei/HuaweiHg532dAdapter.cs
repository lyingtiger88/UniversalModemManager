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
            var hasSession = HasAuthenticatedSessionCookie();

            return new ModemAuthenticationState(
                Supported: true,
                IsLoggedIn: hasSession || !loginVisible,
                IsLocked: false,
                RemainingWaitSeconds: 0,
                Detail: hasSession || !loginVisible
                    ? "HG532d web session appears authenticated."
                    : "HG532d web login is available.");
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

            if (HasAuthenticatedSessionCookie() ||
                !LooksLikeLoginPage(loginPage))
            {
                return SuccessfulLogin(
                    "The HG532d session is already authenticated.");
            }

            // Older HG532 firmware variants do not all use the same login
            // transform. Read the actual page + same-origin JS before choosing.
            var loginSource =
                await BuildLoginSourceAsync(
                    loginPage,
                    cancellationToken);

            var action =
                DetectLoginAction(loginPage) ??
                DetectLoginAction(loginSource) ??
                "/index/login.cgi";

            var fields =
                ExtractHiddenFields(loginPage);

            foreach (var pair in ExtractHiddenFields(loginSource))
                fields[pair.Key] = pair.Value;

            fields["Username"] =
                username.Trim();

            var challenge =
                await ResolveChallengeAsync(
                    loginSource,
                    cancellationToken);

            if (UsesChallengeLogin(loginSource))
            {
                if (string.IsNullOrWhiteSpace(challenge))
                {
                    return Failure(
                        "This HG532d firmware uses challenge-response login, but the current adapter could not extract the challenge token from its login page/scripts. No additional password attempts were sent.");
                }

                fields["challange"] = challenge;
                fields["Password"] =
                    EncodeChallengeHuaweiPassword(
                        password,
                        challenge);
            }
            else if (PageUsesHashedPassword(loginSource))
            {
                fields["Password"] =
                    EncodeLegacyHuaweiPassword(password);
            }
            else
            {
                fields["Password"] = password;
            }

            // Huawei HG53x firmware expects different navigation cookies for
            // admin vs limited-user accounts.
            SetCookie("Language", "en");

            var isAdmin =
                username.Trim().Equals(
                    "admin",
                    StringComparison.OrdinalIgnoreCase);

            SetCookie(
                "FirstMenu",
                isAdmin ? "Admin_0" : "User_2");
            SetCookie(
                "SecondMenu",
                isAdmin ? "Admin_0_0" : "User_2_1");
            SetCookie(
                "ThirdMenu",
                isAdmin ? "Admin_0_0_0" : "User_2_1_0");

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    action)
                {
                    Content =
                        new FormUrlEncodedContent(fields)
                };

            request.Headers.Referrer =
                new Uri(
                    _baseUri!,
                    "/html/index.asp");

            using var response =
                await _http!.SendAsync(
                    request,
                    cancellationToken);

            var responseBody =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            var explicitSuccess =
                responseBody.Contains(
                    "var pageName = '/html/content.asp'",
                    StringComparison.OrdinalIgnoreCase) ||
                responseBody.Contains(
                    "/html/content.asp",
                    StringComparison.OrdinalIgnoreCase);

            var explicitFailure =
                responseBody.Contains(
                    "var pageName = '/'",
                    StringComparison.OrdinalIgnoreCase) ||
                LooksLikeLoginFailure(responseBody);

            // Several HG532 builds return HTTP 200 for both success and failure.
            // Successful login typically sets SessionID_R3.
            var hasSession =
                HasAuthenticatedSessionCookie();

            var verifyPage =
                await GetTextAsync(
                    "/",
                    cancellationToken,
                    allowFailureStatus: true);

            var authenticated =
                !explicitFailure &&
                (
                    explicitSuccess ||
                    hasSession ||
                    !LooksLikeLoginPage(verifyPage)
                );

            if (authenticated)
            {
                return SuccessfulLogin(
                    "Huawei HG532d web login succeeded.");
            }

            var mode =
                UsesChallengeLogin(loginSource)
                    ? "challenge-response"
                    : PageUsesHashedPassword(loginSource)
                        ? "SHA-256/Base64"
                        : "plain form";

            return Failure(
                $"The HG532d rejected the login using its detected {mode} method. Verify the actual web-interface credentials. Factory credentials vary by firmware/provider (commonly user/user on Huawei retail documentation and admin/admin on some ISP builds).");
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
                "type\\s*=\\s*[\'\"]?password",
                RegexOptions.IgnoreCase);

        var hasLoginAction =
            html.Contains(
                "/index/login.cgi",
                StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                html,
                "action\\s*=\\s*[\'\"][^\'\"]*login",
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
                "<form[^>]*action\\s*=\\s*[\'\"](?<action>[^\'\"]+)[\'\"]",
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
                "<input\\b[^>]*type\\s*=\\s*[\'\"]?hidden[\'\"]?[^>]*>",
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
                $"\\b{Regex.Escape(attribute)}\\s*=\\s*[\'\"](?<value>[^\'\"]*)[\'\"]",
                RegexOptions.IgnoreCase);

        return match.Success
            ? WebUtility.HtmlDecode(
                match.Groups["value"].Value)
            : null;
    }

    private async Task<string> BuildLoginSourceAsync(
        string rootPage,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>
        {
            rootPage
        };

        try
        {
            var indexPage =
                await GetTextAsync(
                    "/html/index.asp",
                    cancellationToken,
                    allowFailureStatus: true);

            if (!string.IsNullOrWhiteSpace(indexPage))
                parts.Add(indexPage);
        }
        catch
        {
            // Optional page on some firmware variants.
        }

        var combined =
            string.Join("\n", parts);

        var scripts =
            Regex.Matches(
                combined,
                "<script\\b[^>]*src\\s*=\\s*[\'\"](?<src>[^\'\"]+)[\'\"][^>]*>",
                RegexOptions.IgnoreCase |
                RegexOptions.Singleline)
            .Select(match =>
                WebUtility.HtmlDecode(
                    match.Groups["src"].Value))
            .Where(src =>
                !string.IsNullOrWhiteSpace(src))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        foreach (var src in scripts)
        {
            if (!IsSafeLocalResource(src))
                continue;

            try
            {
                var script =
                    await GetTextAsync(
                        NormalizeLocalPath(src),
                        cancellationToken,
                        allowFailureStatus: true);

                if (!string.IsNullOrWhiteSpace(script))
                    parts.Add(script);
            }
            catch
            {
                // A missing optional script should not abort login detection.
            }
        }

        return string.Join("\n", parts);
    }

    private static bool IsSafeLocalResource(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Uri.TryCreate(
                value,
                UriKind.Absolute,
                out _) ||
            value.StartsWith(
                "//",
                StringComparison.Ordinal) ||
            value.Contains('\\'))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var pathOnly =
            decoded.Split(
                '?',
                2)[0]
                .Split(
                    '#',
                    2)[0];

        return !pathOnly
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                segment == ".." ||
                segment == ".");
    }

    private static string NormalizeLocalPath(
        string value)
    {
        var path =
            value.Split(
                '?',
                2)[0];

        return path.StartsWith(
            "/",
            StringComparison.Ordinal)
                ? path
                : "/" + path.TrimStart('/');
    }

    private static bool UsesChallengeLogin(
        string html) =>
        html.Contains(
            "SubmitFormWithChallange",
            StringComparison.OrdinalIgnoreCase) ||
        (
            html.Contains(
                "challange",
                StringComparison.OrdinalIgnoreCase) &&
            html.Contains(
                "SHA256",
                StringComparison.OrdinalIgnoreCase)
        );

    private async Task<string?> ResolveChallengeAsync(
        string loginSource,
        CancellationToken cancellationToken)
    {
        var inline =
            ExtractChallenge(loginSource);

        if (!string.IsNullOrWhiteSpace(inline))
            return inline;

        // Some HG53x builds fetch the challenge through XMLHttpRequest instead
        // of embedding it in the login page. Inspect only same-origin request
        // targets that appear close to challenge-related script code.
        var xhrMatches =
            Regex.Matches(
                loginSource,
                "open\\s*\\(\\s*['\"](?<method>GET|POST)['\"]\\s*,\\s*['\"](?<url>[^'\"]+)['\"]",
                RegexOptions.IgnoreCase |
                RegexOptions.Singleline);

        foreach (Match match in xhrMatches)
        {
            var url =
                WebUtility.HtmlDecode(
                    match.Groups["url"].Value)
                .Trim();

            if (string.IsNullOrWhiteSpace(url) ||
                !IsSafeLocalResource(url))
            {
                continue;
            }

            var contextStart =
                Math.Max(
                    0,
                    match.Index - 700);

            var contextLength =
                Math.Min(
                    loginSource.Length - contextStart,
                    match.Length + 1400);

            var context =
                loginSource.Substring(
                    contextStart,
                    contextLength);

            var challengeRelated =
                context.Contains(
                    "challange",
                    StringComparison.OrdinalIgnoreCase) ||
                context.Contains(
                    "challenge",
                    StringComparison.OrdinalIgnoreCase);

            if (!challengeRelated)
                continue;

            try
            {
                if (_http is null)
                    break;

                var method =
                    match.Groups["method"].Value.Equals(
                        "POST",
                        StringComparison.OrdinalIgnoreCase)
                        ? HttpMethod.Post
                        : HttpMethod.Get;

                using var request =
                    new HttpRequestMessage(
                        method,
                        NormalizeLocalPath(url));

                if (method == HttpMethod.Post)
                {
                    request.Content =
                        new FormUrlEncodedContent(
                            Array.Empty<
                                KeyValuePair<string, string>>());
                }

                using var response =
                    await _http.SendAsync(
                        request,
                        cancellationToken);

                var body =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                var extracted =
                    ExtractChallenge(body);

                if (!string.IsNullOrWhiteSpace(extracted))
                    return extracted;

                var trimmed =
                    WebUtility.HtmlDecode(body)
                        .Trim()
                        .Trim(
                            '\'',
                            '\"',
                            ' ',
                            '\r',
                            '\n',
                            '\t');

                if (Regex.IsMatch(
                        trimmed,
                        "^[A-Za-z0-9]{8,128}$"))
                {
                    return trimmed;
                }
            }
            catch
            {
                // Continue with the next candidate request.
            }
        }

        return null;
    }

    private static string? ExtractChallenge(
        string html)
    {
        string[] patterns =
        [
            "\\bchallange\\s*=\\s*[\'\"](?<value>[A-Za-z0-9]{8,128})[\'\"]",
            "\\bchallenge\\s*=\\s*[\'\"](?<value>[A-Za-z0-9]{8,128})[\'\"]",
            "name\\s*=\\s*[\'\"]challange[\'\"][^>]*value\\s*=\\s*[\'\"](?<value>[^\'\"]+)[\'\"]",
            "value\\s*=\\s*[\'\"](?<value>[^\'\"]+)[\'\"][^>]*name\\s*=\\s*[\'\"]challange[\'\"]"
        ];

        foreach (var pattern in patterns)
        {
            var match =
                Regex.Match(
                    html,
                    pattern,
                    RegexOptions.IgnoreCase |
                    RegexOptions.Singleline);

            if (match.Success)
            {
                var value =
                    WebUtility.HtmlDecode(
                        match.Groups["value"].Value)
                    .Trim();

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private bool HasAuthenticatedSessionCookie()
    {
        if (_cookies is null ||
            _baseUri is null)
        {
            return false;
        }

        try
        {
            return _cookies
                .GetCookies(_baseUri)
                .Cast<Cookie>()
                .Any(cookie =>
                    cookie.Name.Equals(
                        "SessionID_R3",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(
                        cookie.Value));
        }
        catch
        {
            return false;
        }
    }

    private static string EncodeChallengeHuaweiPassword(
        string password,
        string challenge)
    {
        var firstHex =
            Sha256Hex(password);

        var firstBase64 =
            Convert.ToBase64String(
                Encoding.ASCII.GetBytes(
                    firstHex));

        return Sha256Hex(
            firstBase64 + challenge);
    }

    private static string Sha256Hex(
        string value)
    {
        var digest =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    value));

        return Convert.ToHexString(digest)
            .ToLowerInvariant();
    }

    private static ModemLoginResult SuccessfulLogin(
        string message) =>
        new(
            true,
            message,
            new ModemAuthenticationState(
                true,
                true,
                false,
                0,
                "Authenticated HG532d web session."));

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
        string password) =>
        Convert.ToBase64String(
            Encoding.ASCII.GetBytes(
                Sha256Hex(password)));

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
