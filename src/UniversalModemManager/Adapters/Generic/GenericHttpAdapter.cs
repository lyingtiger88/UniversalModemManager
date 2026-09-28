using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using UniversalModemManager.Core;
using UniversalModemManager.Models;

namespace UniversalModemManager.Adapters.Generic;

public sealed class GenericHttpAdapter : IModemAdapter
{
    private static readonly string[] ProbePaths =
    [
        "",
        "index.html",
        "index.htm",
        "login.html",
        "login.htm",
        "login.asp",
        "login.cgi",
        "main.html",
        "html/index.html",
        "userRpm/LoginRpm.htm"
    ];

    private static readonly (string Brand, string[] Markers)[] BrandMarkers =
    [
        ("TP-Link", ["tp-link", "tplink", "tp_link", "td-w", "archer vr"]),
        ("D-Link", ["d-link", "dlink", "dsl-2", "dsl-3"]),
        ("Zyxel", ["zyxel", "zyxel communications", "vmg", "p-660"]),
        ("Tenda", ["tenda", "d151", "d301", "v300"]),
        ("Huawei", ["huawei", "echolife", "hg532d", "hg5", "hg6", "ws3"]),
        ("ZTE", ["zte", "zxhn", "h108", "h168"]),
        ("ASUS", ["asus", "dsl-ac", "dsl-n"]),
        ("NETGEAR", ["netgear", "dgn", "dm200"]),
        ("Technicolor", ["technicolor", "thomson", "tg5", "tg7"]),
        ("Sagemcom", ["sagemcom", "fast 5", "fast 3"]),
        ("FiberHome", ["fiberhome", "an5506"]),
        ("Nokia", ["nokia", "g-240", "g-140"]),
        ("MERCUSYS", ["mercusys"]),
        ("MikroTik", ["mikrotik", "routeros"]),
        ("Linksys", ["linksys"]),
        ("TRENDnet", ["trendnet"]),
        ("Edimax", ["edimax"]),
        ("DrayTek", ["draytek", "vigor"]),
        ("Sercomm", ["sercomm"]),
        ("Arcadyan", ["arcadyan"])
    ];

    private static readonly string[] DslMarkers =
    [
        "adsl",
        "vdsl",
        "xdsl",
        "dsl router",
        "dsl modem",
        "broadband router",
        "wan dsl",
        "atm pvc",
        "pppoe",
        "pppoa"
    ];

    private static readonly string[] KnownModelPrefixes =
    [
        "TD-W", "TD-8", "TD-9", "Archer VR",
        "DSL-", "DIR-",
        "VMG", "P-660", "P660",
        "HG532d", "HG5", "HG6", "EchoLife",
        "ZXHN", "H108", "H168",
        "DSL-AC", "DSL-N",
        "DGN", "DM200",
        "Vigor",
        "TG5", "TG7",
        "FAST ",
        "AN5506"
    ];

    public string Id => "generic.http";
    public string DisplayName => "Generic HTTP/DSL router";

    // Generic means we know the device is a router/modem and can identify it,
    // but we do not claim vendor-specific control APIs that have not been verified.
    public ModemCapability Capabilities =>
        ModemCapability.Identity;

    public async Task<ModemProbeResult> ProbeAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var evidence = await CollectEvidenceAsync(
                candidate.Gateway,
                cancellationToken);

            if (!evidence.Reachable)
            {
                return new ModemProbeResult(
                    Reachable: false,
                    MatchesSelectedProfile: false,
                    DetectedManufacturer: null,
                    DetectedModel: null,
                    Detail: evidence.Detail);
            }

            var brand = DetectBrand(evidence);
            var model = DetectModel(evidence);
            var isDsl = DetectDsl(evidence);

            var explicitModel =
                !candidate.Model.Equals(
                    "Auto detect",
                    StringComparison.OrdinalIgnoreCase) &&
                !candidate.Model.Equals(
                    "Other / Unknown",
                    StringComparison.OrdinalIgnoreCase);

            if (explicitModel &&
                (string.IsNullOrWhiteSpace(model) ||
                 model.Equals(
                     "ADSL/VDSL Router",
                     StringComparison.OrdinalIgnoreCase) ||
                 model.Equals(
                     "Router / Modem",
                     StringComparison.OrdinalIgnoreCase)))
            {
                model = candidate.Model;
            }

            if (string.IsNullOrWhiteSpace(model) && isDsl)
                model = "ADSL/VDSL Router";

            if (string.IsNullOrWhiteSpace(model))
                model = "Router / Modem";

            var manufacturer =
                brand ??
                (!IsAutoOrGeneric(candidate.Manufacturer)
                    ? candidate.Manufacturer
                    : "Generic");

            var selectedGeneric =
                IsAutoOrGeneric(candidate.Manufacturer) ||
                candidate.Model.Equals(
                    "Auto detect",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.Model.Equals(
                    "Other / Unknown",
                    StringComparison.OrdinalIgnoreCase);

            var brandMatch =
                selectedGeneric ||
                string.IsNullOrWhiteSpace(brand) ||
                brand.Equals(
                    candidate.Manufacturer,
                    StringComparison.OrdinalIgnoreCase);

            var detailParts = new List<string>
            {
                evidence.Detail ?? "Router web interface detected."
            };

            if (!string.IsNullOrWhiteSpace(evidence.Title))
                detailParts.Add($"Title: {evidence.Title}");

            if (!string.IsNullOrWhiteSpace(evidence.AuthRealm))
                detailParts.Add($"Auth realm: {evidence.AuthRealm}");

            if (!string.IsNullOrWhiteSpace(evidence.Server))
                detailParts.Add($"Server: {evidence.Server}");

            detailParts.Add(
                isDsl
                    ? "DSL-style router interface detected."
                    : "Generic router web interface detected.");

            return new ModemProbeResult(
                Reachable: true,
                MatchesSelectedProfile: brandMatch,
                DetectedManufacturer: manufacturer,
                DetectedModel: model,
                Detail: string.Join(" • ", detailParts));
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
        var probe =
            await ProbeAsync(
                candidate,
                cancellationToken);

        if (!probe.Reachable)
            return null;

        return new ModemIdentity(
            Manufacturer:
                probe.DetectedManufacturer ??
                (IsAutoOrGeneric(candidate.Manufacturer)
                    ? "Generic"
                    : candidate.Manufacturer),
            Model:
                !candidate.Model.Equals(
                    "Auto detect",
                    StringComparison.OrdinalIgnoreCase) &&
                !candidate.Model.Equals(
                    "Other / Unknown",
                    StringComparison.OrdinalIgnoreCase)
                    ? candidate.Model
                    : probe.DetectedModel ??
                      "Router / Modem",
            Firmware: null,
            Gateway:
                NormalizeBaseUri(candidate.Gateway).ToString(),
            AdapterId: Id);
    }

    private static async Task<ProbeEvidence> CollectEvidenceAsync(
        Uri gateway,
        CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(gateway);

        // Many ADSL/VDSL routers ship with expired/self-signed HTTPS certificates.
        // This local-only probe accepts them so detection still works. No credentials
        // are sent by this adapter.
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };

        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "UniversalModemManager",
                "0.2"));

        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/html"));

        var attempts = BuildProbeUris(baseUri);
        var bodies = new List<string>();
        string? title = null;
        string? realm = null;
        string? server = null;
        string? successfulAddress = null;
        HttpStatusCode? bestStatus = null;
        Exception? lastError = null;

        foreach (var uri in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        uri);

                using var response =
                    await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                bestStatus ??= response.StatusCode;
                successfulAddress ??=
                    response.RequestMessage?.RequestUri?.ToString()
                    ?? uri.ToString();

                server ??=
                    response.Headers.Server.ToString();

                realm ??=
                    ExtractRealm(response);

                // 401/403 is still strong evidence that a router admin endpoint exists.
                var body =
                    await SafeReadBodyAsync(
                        response,
                        cancellationToken);

                if (!string.IsNullOrWhiteSpace(body))
                {
                    bodies.Add(body);

                    title ??=
                        ExtractTitle(body);
                }

                if (response.StatusCode is
                    HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden)
                {
                    // No reason to keep probing many paths when the root admin UI
                    // is clearly present but protected.
                    if (uri.AbsolutePath == "/" ||
                        !string.IsNullOrWhiteSpace(realm))
                    {
                        break;
                    }
                }

                if (response.IsSuccessStatusCode &&
                    !string.IsNullOrWhiteSpace(body))
                {
                    // Continue a few paths only when root HTML was too generic.
                    if (DetectBrandFromText(
                            string.Join("\n", bodies)) is not null ||
                        DetectDslFromText(
                            string.Join("\n", bodies)))
                    {
                        break;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex)
                when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        // If HTTP failed completely, retry the same host over HTTPS.
        if (successfulAddress is null &&
            baseUri.Scheme.Equals(
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase))
        {
            var secure =
                new UriBuilder(baseUri)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Port = -1
                }.Uri;

            foreach (var uri in BuildProbeUris(secure))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var response =
                        await http.GetAsync(
                            uri,
                            cancellationToken);

                    bestStatus ??= response.StatusCode;
                    successfulAddress ??=
                        response.RequestMessage?.RequestUri?.ToString()
                        ?? uri.ToString();

                    server ??=
                        response.Headers.Server.ToString();

                    realm ??=
                        ExtractRealm(response);

                    var body =
                        await SafeReadBodyAsync(
                            response,
                            cancellationToken);

                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        bodies.Add(body);
                        title ??=
                            ExtractTitle(body);
                    }

                    break;
                }
                catch (Exception ex)
                    when (ex is HttpRequestException or TaskCanceledException)
                {
                    lastError = ex;
                }
            }
        }

        if (successfulAddress is null)
        {
            return new ProbeEvidence(
                Reachable: false,
                Body: string.Empty,
                Title: null,
                AuthRealm: null,
                Server: null,
                Detail:
                    lastError?.Message ??
                    "No HTTP or HTTPS response was received from the router.");
        }

        var allText =
            string.Join("\n", bodies);

        return new ProbeEvidence(
            Reachable: true,
            Body: allText,
            Title: title,
            AuthRealm: realm,
            Server: server,
            Detail:
                $"Admin web interface responded at {successfulAddress}" +
                (bestStatus is null
                    ? string.Empty
                    : $" with HTTP {(int)bestStatus.Value} {bestStatus.Value}."));
    }

    private static IEnumerable<Uri> BuildProbeUris(
        Uri baseUri)
    {
        foreach (var path in ProbePaths)
        {
            if (string.IsNullOrEmpty(path))
            {
                yield return baseUri;
                continue;
            }

            yield return new Uri(
                baseUri,
                path);
        }
    }

    private static async Task<string> SafeReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var mediaType =
                response.Content.Headers.ContentType?.MediaType;

            if (mediaType is not null &&
                !mediaType.Contains(
                    "html",
                    StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains(
                    "text",
                    StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains(
                    "xml",
                    StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains(
                    "javascript",
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var text =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            const int maxChars = 128_000;

            return text.Length > maxChars
                ? text[..maxChars]
                : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ExtractRealm(
        HttpResponseMessage response)
    {
        foreach (var challenge
                 in response.Headers.WwwAuthenticate)
        {
            var parameter =
                challenge.Parameter;

            if (string.IsNullOrWhiteSpace(parameter))
                continue;

            var match =
                Regex.Match(
                    parameter,
                    "realm\\s*=\\s*[\"']?(?<realm>[^\"']+)",
                    RegexOptions.IgnoreCase);

            if (match.Success)
                return match.Groups["realm"].Value.Trim();
        }

        return null;
    }

    private static string? ExtractTitle(
        string body)
    {
        var match =
            Regex.Match(
                body,
                "<title[^>]*>(?<title>.*?)</title>",
                RegexOptions.IgnoreCase |
                RegexOptions.Singleline);

        if (!match.Success)
            return null;

        var title =
            Regex.Replace(
                match.Groups["title"].Value,
                "<.*?>",
                string.Empty);

        title =
            WebUtility.HtmlDecode(title)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

        return string.IsNullOrWhiteSpace(title)
            ? null
            : title;
    }

    private static string? DetectBrand(
        ProbeEvidence evidence)
    {
        var haystack =
            BuildHaystack(evidence);

        return DetectBrandFromText(haystack);
    }

    private static string? DetectBrandFromText(
        string text)
    {
        foreach (var (brand, markers)
                 in BrandMarkers)
        {
            if (markers.Any(marker =>
                    text.Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return brand;
            }
        }

        return null;
    }

    private static bool DetectDsl(
        ProbeEvidence evidence) =>
        DetectDslFromText(
            BuildHaystack(evidence));

    private static bool DetectDslFromText(
        string text) =>
        DslMarkers.Any(marker =>
            text.Contains(
                marker,
                StringComparison.OrdinalIgnoreCase));

    private static string? DetectModel(
        ProbeEvidence evidence)
    {
        var haystack =
            BuildHaystack(evidence);

        // First try explicit model-like labels commonly embedded in router HTML/JS.
        string[] patterns =
        [
            "(?:model(?:name|no|number)?|product(?:name|model)?)\\s*[:=]\\s*[\\\"']?(?<model>[A-Za-z0-9][A-Za-z0-9._\\-/ ]{2,30})",
            "(?:device_name|devicename)\\s*[:=]\\s*[\\\"'](?<model>[^\\\"']{3,32})[\\\"']"
        ];

        foreach (var pattern in patterns)
        {
            var match =
                Regex.Match(
                    haystack,
                    pattern,
                    RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var model =
                    CleanModel(
                        match.Groups["model"].Value);

                if (!string.IsNullOrWhiteSpace(model))
                    return model;
            }
        }

        // Then scan for well-known DSL/router model prefixes.
        foreach (var prefix in KnownModelPrefixes)
        {
            var match =
                Regex.Match(
                    haystack,
                    Regex.Escape(prefix) +
                    @"[A-Za-z0-9._\-/]{0,20}",
                    RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var model =
                    CleanModel(match.Value);

                if (!string.IsNullOrWhiteSpace(model))
                    return model;
            }
        }

        // Page titles are often the only clue on older ADSL firmware.
        if (!string.IsNullOrWhiteSpace(evidence.Title))
        {
            var title =
                CleanModel(evidence.Title);

            if (title.Length is >= 3 and <= 48 &&
                !title.Equals(
                    "Login",
                    StringComparison.OrdinalIgnoreCase) &&
                !title.Equals(
                    "Router",
                    StringComparison.OrdinalIgnoreCase))
            {
                return title;
            }
        }

        return null;
    }

    private static string CleanModel(
        string value)
    {
        var cleaned =
            WebUtility.HtmlDecode(value)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim(
                    ' ',
                    '\t',
                    ':',
                    '=',
                    '\'',
                    '\"',
                    ';',
                    ',');

        cleaned =
            Regex.Replace(
                cleaned,
                @"\s+",
                " ");

        string[] trailingWords =
        [
            "login",
            "configuration",
            "management",
            "web interface",
            "router"
        ];

        foreach (var suffix in trailingWords)
        {
            if (cleaned.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase) &&
                cleaned.Length > suffix.Length + 2)
            {
                cleaned =
                    cleaned[..^suffix.Length].Trim();
            }
        }

        return cleaned.Length > 48
            ? cleaned[..48].Trim()
            : cleaned;
    }

    private static string BuildHaystack(
        ProbeEvidence evidence) =>
        string.Join(
            "\n",
            new[]
            {
                evidence.Title,
                evidence.AuthRealm,
                evidence.Server,
                evidence.Body
            }.Where(x =>
                !string.IsNullOrWhiteSpace(x)));

    private static bool IsAutoOrGeneric(
        string manufacturer) =>
        manufacturer.Equals(
            "Auto detect",
            StringComparison.OrdinalIgnoreCase) ||
        manufacturer.Equals(
            "Generic",
            StringComparison.OrdinalIgnoreCase);

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

    private sealed record ProbeEvidence(
        bool Reachable,
        string Body,
        string? Title,
        string? AuthRealm,
        string? Server,
        string? Detail);
}
