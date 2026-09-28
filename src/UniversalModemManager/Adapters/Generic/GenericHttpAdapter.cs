using System.Net;
using System.Net.Http.Headers;
using UniversalModemManager.Core;
using UniversalModemManager.Models;

namespace UniversalModemManager.Adapters.Generic;

public sealed class GenericHttpAdapter : IModemAdapter
{
    public string Id => "generic.http";
    public string DisplayName => "Generic HTTP router";
    public ModemCapability Capabilities =>
        ModemCapability.Identity |
        ModemCapability.NetworkStatus;

    public async Task<ModemProbeResult> ProbeAsync(
        ModemCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(4)
            };

            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("UniversalModemManager", "0.1"));

            using var response = await http.GetAsync(candidate.Gateway, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var sample = body.Length > 32_000 ? body[..32_000] : body;

            var detectedBrand = DetectBrand(response, sample);
            var detectedModel = DetectModel(sample);

            var selectedGeneric =
                candidate.Manufacturer.Equals("Generic", StringComparison.OrdinalIgnoreCase) ||
                candidate.Model.Equals("Auto detect", StringComparison.OrdinalIgnoreCase) ||
                candidate.Model.Equals("Other / Unknown", StringComparison.OrdinalIgnoreCase);

            var brandMatch = selectedGeneric ||
                string.IsNullOrWhiteSpace(detectedBrand) ||
                detectedBrand.Equals(candidate.Manufacturer, StringComparison.OrdinalIgnoreCase);

            return new ModemProbeResult(
                Reachable: true,
                MatchesSelectedProfile: brandMatch,
                DetectedManufacturer: detectedBrand,
                DetectedModel: detectedModel,
                Detail: $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
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
            candidate.Manufacturer,
            candidate.Model,
            Firmware: null,
            Gateway: candidate.Gateway.ToString(),
            AdapterId: Id);

        return Task.FromResult<ModemIdentity?>(identity);
    }

    private static string? DetectBrand(HttpResponseMessage response, string body)
    {
        var server = response.Headers.Server.ToString();
        var haystack = $"{server}
{body}";

        if (haystack.Contains("Huawei", StringComparison.OrdinalIgnoreCase))
            return "Huawei";
        if (haystack.Contains("ZTE", StringComparison.OrdinalIgnoreCase))
            return "ZTE";
        if (haystack.Contains("TP-Link", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("TPLink", StringComparison.OrdinalIgnoreCase))
            return "TP-Link";
        if (haystack.Contains("D-Link", StringComparison.OrdinalIgnoreCase))
            return "D-Link";
        if (haystack.Contains("Tenda", StringComparison.OrdinalIgnoreCase))
            return "Tenda";

        return null;
    }

    private static string? DetectModel(string body)
    {
        string[] markers =
        [
            "E5573", "E5577", "B315", "B525", "B612", "B818",
            "MF286", "MC801", "MC888",
            "MR600", "MR6400", "M7350"
        ];

        return markers.FirstOrDefault(marker =>
            body.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
