using System.IO;

namespace SVNAssist.Services;

/// <summary>
/// Riconosce dal nome i file che di solito contengono segreti (chiavi, certificati, configurazioni d'ambiente),
/// per avvisare l'utente prima che il loro diff venga inviato a un provider AI esterno. È solo un avviso: euristica sul nome.
/// </summary>
public static class SensitiveFiles
{
    private static readonly string[] SensitiveExtensions =
        [".pfx", ".p12", ".pem", ".key", ".jks", ".keystore", ".ppk", ".kdbx"];

    /// <summary><c>true</c> se il nome del file suggerisce che contenga segreti.</summary>
    public static bool LooksSensitive(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/')).ToLowerInvariant();
        if (name.Length == 0)
            return false;

        if (name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal))
            return true;

        if (SensitiveExtensions.Contains(Path.GetExtension(name)))
            return true;

        if (name.Contains("secret", StringComparison.Ordinal))
            return true;

        if (name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519")
            return true;

        // appsettings.Production.json, appsettings.Local.json... (non il semplice appsettings.json)
        return name.StartsWith("appsettings.", StringComparison.Ordinal)
               && name.EndsWith(".json", StringComparison.Ordinal)
               && name.Length > "appsettings..json".Length;
    }

    /// <summary>I percorsi sospetti tra quelli indicati.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> paths) => paths.Where(LooksSensitive).ToList();
}
