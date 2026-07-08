using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace NetOptimizer.Services;

/// <summary>
/// Reports whether an executable carries an Authenticode signature.
/// (Presence of a signature — not full trust-chain validation.)
/// Results are cached by path.
/// </summary>
public static class SignatureService
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        return Cache.GetOrAdd(path, Check);
    }

    private static string Check(string path)
    {
        try
        {
            // Throws if the file has no embedded Authenticode signature.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return "Подписан";
        }
        catch
        {
            return "Не подписан";
        }
    }
}
