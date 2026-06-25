using System.IO;

namespace SVNAssist.Services;

/// <summary>
/// Recupera automaticamente un token GitHub da usare con GitHub Models,
/// senza che l'utente debba configurare nulla nelle opzioni.
/// </summary>
/// <remarks>
/// Ordine delle sorgenti:
/// 1. <c>gh auth token</c> — se GitHub CLI è installato e l'utente ha fatto login
/// 2. variabili d'ambiente <c>GITHUB_TOKEN</c> / <c>GH_TOKEN</c>
///
/// Così, per la maggior parte degli sviluppatori GitHub, la generazione del commit
/// "funziona e basta" senza incollare un PAT né aprire Tools &gt; Options.
/// Il token risolto viene messo in cache per la sessione (evita di lanciare gh ad ogni commit).
///
/// La scoperta dell'eseguibile <c>gh</c> e la lettura delle variabili d'ambiente sono
/// iniettabili (parametri opzionali del costruttore) per rendere la classe unit-testabile.
/// </remarks>
internal interface IGitHubTokenResolver
{
    /// <summary>Restituisce un token GitHub, o <c>null</c> se nessuna sorgente lo fornisce.</summary>
    Task<string?> ResolveAsync(CancellationToken ct);
}

/// <inheritdoc />
internal sealed class GitHubTokenResolver : IGitHubTokenResolver
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<string, string?> _environmentReader;
    private readonly Func<string?> _ghLocator;
    private string? _cachedToken;

    public GitHubTokenResolver(
        IProcessRunner processRunner,
        Func<string, string?>? environmentReader = null,
        Func<string?>? ghLocator = null)
    {
        _processRunner = processRunner;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _ghLocator = ghLocator ?? FindGhExecutable;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken))
            return _cachedToken;

        var token = await TryFromGhCliAsync(ct) ?? TryFromEnvironment();

        if (!string.IsNullOrWhiteSpace(token))
            _cachedToken = token;

        return token;
    }

    private async Task<string?> TryFromGhCliAsync(CancellationToken ct)
    {
        var gh = _ghLocator();
        if (string.IsNullOrWhiteSpace(gh))
            return null;

        try
        {
            var result = await _processRunner.RunAsync(gh, ["auth", "token"], workingDirectory: null, ct);
            if (result.ExitCode != 0)
                return null;

            var token = result.StandardOutput.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            // gh non avviabile / non loggato: passiamo alla sorgente successiva.
            return null;
        }
    }

    private string? TryFromEnvironment()
    {
        foreach (var name in (string[])["GITHUB_TOKEN", "GH_TOKEN"])
        {
            var value = _environmentReader(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    /// <summary>Cerca <c>gh.exe</c> nel PATH e nella posizione di installazione standard.</summary>
    private static string? FindGhExecutable()
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var candidate = Path.Combine(dir, "gh.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var known = Path.Combine(programFiles, "GitHub CLI", "gh.exe");
        return File.Exists(known) ? known : null;
    }
}
