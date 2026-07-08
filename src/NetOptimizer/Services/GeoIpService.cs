using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace NetOptimizer.Services;

/// <summary>
/// Looks up the country of a remote IP on demand via the free ip-api.com service
/// (no API key; rate-limited to ~45 requests/minute). Results are cached.
/// </summary>
public static class GeoIpService
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<string> LookupAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip) || ip is "*" or "0.0.0.0" or "::" or "127.0.0.1" or "::1")
            return "";

        if (Cache.TryGetValue(ip, out var cached))
            return cached;

        try
        {
            string json = await Http.GetStringAsync($"http://ip-api.com/json/{ip}?fields=status,country,countryCode");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string status = root.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
            if (status != "success")
            {
                Cache[ip] = "—";
                return "—";
            }

            string country = root.TryGetProperty("country", out var c) ? (c.GetString() ?? "") : "";
            string code = root.TryGetProperty("countryCode", out var cc) ? (cc.GetString() ?? "") : "";
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
