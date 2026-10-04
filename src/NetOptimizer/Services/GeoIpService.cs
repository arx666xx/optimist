using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace NetOptimizer.Services;

/// <summary>
/// Looks up the country of a remote IP on demand via the free ipwho.is service
/// (no API key, HTTPS). The previous provider was plain HTTP only, which sent the
/// list of addresses this PC talks to across the network in the clear.
/// Results are cached.
/// </summary>
public static class GeoIpService
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<string> LookupAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip) || ip is "*" or "0.0.0.0" or "::" or "127.0.0.1" or "::1")
            return "";

        // Link-local IPv6 carries a scope suffix ("fe80::1%12") — never a public host.
        if (ip.Contains('%')) return "";

        if (Cache.TryGetValue(ip, out var cached))
            return cached;

        try
        {
            string json = await Http.GetStringAsync($"https://ipwho.is/{ip}?fields=success,country,country_code");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool ok = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                Cache[ip] = "—";
                return "—";
            }

            string country = root.TryGetProperty("country", out var c) ? (c.GetString() ?? "") : "";
            string code = root.TryGetProperty("country_code", out var cc) ? (cc.GetString() ?? "") : "";
            string result = code.Length == 2 ? $"{FlagEmoji(code)} {country}" : country;

            Cache[ip] = result;
            return result;
        }
        catch
        {
            return "—";
        }
    }

    private static string FlagEmoji(string code)
    {
        // Map "US" -> 🇺🇸 using regional indicator symbols.
        code = code.ToUpperInvariant();
        return char.ConvertFromUtf32(0x1F1E6 + (code[0] - 'A'))
             + char.ConvertFromUtf32(0x1F1E6 + (code[1] - 'A'));
    }
}
