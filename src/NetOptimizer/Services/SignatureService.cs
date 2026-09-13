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
    private static readonly ConcurrentDictionary<string, string> SubjectCache = new();

    public static string Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        return Cache.GetOrAdd(path, Check);
    }

    /// <summary>
    /// Returns the signing certificate's subject, or "" when the file is unsigned.
    /// Used by the updater to make sure a signed build is not replaced by a
    /// build signed by somebody else.
    /// </summary>
    public static string GetSubject(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        return SubjectCache.GetOrAdd(path, ReadSubject);
    }

    private static string Check(string path) => ReadSubject(path).Length > 0 ? "Подписан" : "Не подписан";

    private static string ReadSubject(string path)
    {
        try
        {
            // Throws if the file has no embedded Authenticode signature.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.Subject ?? "";
        }
        catch
        {
            return "";
        }
    }
}
