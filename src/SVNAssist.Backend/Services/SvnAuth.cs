using System.Globalization;

namespace SVNAssist.Services;

/// <summary>Credenziali chieste all'utente (mai scritte nei log né nello stato UI).</summary>
/// <param name="Remember"><c>true</c>: svn le salva nella sua cache (niente <c>--no-auth-cache</c>).</param>
public sealed record SvnCredentials(string UserName, string Password, bool Remember)
{
    // Un record stampato nei log o nelle eccezioni non deve mai rivelare la password.
    public override string ToString() => $"SvnCredentials {{ UserName = {UserName}, Password = ***, Remember = {Remember} }}";
}

/// <summary>Chiede utente/password all'utente (l'host mostra il dialogo). <c>null</c> = annullato.</summary>
public interface ISvnCredentialPrompt
{
    Task<SvnCredentials?> RequestAsync(string detail, CancellationToken ct);
}

/// <summary>Riconoscimento (funzioni pure) di alcuni errori di svn dal testo di stderr / dal messaggio d'errore.</summary>
public static class SvnErrors
{
    /// <summary>
    /// Autenticazione mancante o rifiutata: E170001 (autorizzazione fallita), E215004 (nessuna altra credenziale),
    /// E215003/E215001, oppure il testo "Authentication failed" / "No more credentials".
    /// </summary>
    public static bool IsAuthenticationError(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("E170001", StringComparison.Ordinal)
               || text.Contains("E215004", StringComparison.Ordinal)
               || text.Contains("E215003", StringComparison.Ordinal)
               || text.Contains("E215001", StringComparison.Ordinal)
               || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Authorization failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("No more credentials", StringComparison.OrdinalIgnoreCase));

    /// <summary>Working copy bloccata da un'operazione interrotta (serve cleanup): E155004, E155037, "run 'cleanup'".</summary>
    public static bool IsWorkingCopyLocked(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("E155004", StringComparison.Ordinal)
               || text.Contains("E155037", StringComparison.Ordinal)
               || text.Contains("run 'cleanup'", StringComparison.OrdinalIgnoreCase)
               || text.Contains("performing 'cleanup'", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>svn lock</c> fallito perché il file è già bloccato da un altro (W160035/E160035, E200035 "already locked").</summary>
    public static bool IsLockedByOther(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("160035", StringComparison.Ordinal)
               || text.Contains("E200035", StringComparison.Ordinal)
               || text.Contains("is already locked", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Validazione della revisione digitata per "SVN Update to revision".</summary>
public static class SvnRevision
{
    /// <summary>Intero positivo (senza segno, anche con zeri iniziali) oppure HEAD (qualsiasi maiuscolo). Restituisce la forma per svn -r.</summary>
    public static bool TryNormalize(string? input, out string revision)
    {
        revision = string.Empty;
        var s = input?.Trim() ?? string.Empty;
        if (s.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            revision = "HEAD";
            return true;
        }

        if (s.Length is 0 or > 18 || !s.All(char.IsAsciiDigit)
            || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return false;

        revision = n.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
