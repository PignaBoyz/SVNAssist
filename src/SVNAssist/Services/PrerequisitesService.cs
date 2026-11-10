using System.IO;

namespace SVNAssist.Services;

/// <summary>Identificatori stabili dei prerequisiti (usati dalla UI per scegliere l'azione).</summary>
internal static class PrerequisiteIds
{
    public const string Svn = "svn";
    public const string TortoiseSvn = "tortoisesvn";
    public const string GitHubCli = "gh-cli";
    public const string GitHubLogin = "gh-login";
    public const string ClaudeCode = "claude-code";
    public const string AnthropicKey = "anthropic-key";
    public const string CopilotRuntime = "copilot-runtime";
}

/// <summary>Un prerequisito con il suo stato e le istruzioni per sistemarlo.</summary>
/// <param name="Id">Vedi <see cref="PrerequisiteIds"/>.</param>
/// <param name="Group">Titolo del gruppo (per l'intestazione nella UI).</param>
/// <param name="Title">Nome del prerequisito.</param>
/// <param name="IsOk">Presente / configurato.</param>
/// <param name="Detail">Stato in una riga (percorso trovato, cosa manca).</param>
/// <param name="Instructions">Comandi/passi esatti da eseguire (mostrati se non è a posto).</param>
/// <param name="PrimaryAction">Etichetta del pulsante principale (<c>null</c> = nessuno).</param>
/// <param name="SecondaryAction">Etichetta del pulsante secondario (<c>null</c> = nessuno).</param>
internal sealed record PrerequisiteItem(
    string Id,
    string Group,
    string Title,
    bool IsOk,
    string Detail,
    string Instructions,
    string? PrimaryAction = null,
    string? SecondaryAction = null);

/// <summary>Esito del controllo dei prerequisiti.</summary>
/// <param name="Items">Prerequisiti nell'ordine di visualizzazione.</param>
/// <param name="ConfiguredProvider">Valore dell'impostazione "AI Provider" (auto, copilot, ...).</param>
/// <param name="UsableProviders">Nomi dei provider AI oggi utilizzabili, nell'ordine in cui verrebbero provati.</param>
internal sealed record PrerequisitesSnapshot(
    IReadOnlyList<PrerequisiteItem> Items,
    string ConfiguredProvider,
    IReadOnlyList<string> UsableProviders)
{
    /// <summary><c>true</c> se manca <c>svn.exe</c> (prerequisito essenziale).</summary>
    public bool SvnMissing => Items.Any(i => i.Id == PrerequisiteIds.Svn && !i.IsOk);

    /// <summary><c>true</c> se almeno un provider AI è utilizzabile.</summary>
    public bool HasAiProvider => UsableProviders.Count > 0;

    /// <summary>Numero di cose da sistemare: svn mancante e/o nessun provider AI.</summary>
    public int ProblemCount => (SvnMissing ? 1 : 0) + (HasAiProvider ? 0 : 1);
}

/// <summary>
/// Sonde sull'ambiente (eseguibili, variabili, runtime): iniettabili per i test.
/// I default sono "tutto assente".
/// </summary>
internal sealed class PrerequisiteProbes
{
    public Func<string?> FindSvn { get; init; } = () => null;
    public Func<string?> FindTortoise { get; init; } = () => null;
    public Func<string?> FindGitHubCli { get; init; } = () => null;
    public Func<string?> FindClaude { get; init; } = () => null;
    public Func<string, string?> ReadEnvironment { get; init; } = _ => null;
    public Func<bool> CopilotRuntimeInstalled { get; init; } = () => false;

    /// <summary>Dato il percorso di gh, dice se l'utente ha fatto login.</summary>
    public Func<string, CancellationToken, Task<bool>> IsGitHubCliLoggedIn { get; init; } = (_, _) => Task.FromResult(false);

    /// <summary>Token GitHub risolto (gh o variabili d'ambiente), <c>null</c> se assente.</summary>
    public Func<CancellationToken, Task<string?>> ResolveGitHubToken { get; init; } = _ => Task.FromResult<string?>(null);

    /// <summary>Sonde reali della macchina.</summary>
    public static PrerequisiteProbes CreateDefault(IProcessRunner processRunner, IGitHubTokenResolver tokenResolver) => new()
    {
        FindSvn = SvnLocator.FindSvn,
        FindTortoise = SvnLocator.FindTortoiseProc,
        FindGitHubCli = FindGh,
        FindClaude = ClaudeCodeAiService.FindClaudeExecutable,
        ReadEnvironment = Environment.GetEnvironmentVariable,
        CopilotRuntimeInstalled = () => CopilotRuntimeInstaller.IsInstalled,
        IsGitHubCliLoggedIn = async (gh, ct) =>
        {
            try
            {
                var result = await processRunner.RunAsync(gh, ["auth", "status"], null, ct);
                return result.ExitCode == 0;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        },
        ResolveGitHubToken = tokenResolver.ResolveAsync,
    };

    private static string? FindGh()
    {
        // Il PATH del processo di VS può non conoscere un'installazione appena fatta: guardiamo anche la cartella standard.
        var known = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        return SvnLocator.FindExecutable("gh.exe") ?? (File.Exists(known) ? known : null);
    }
}

/// <summary>
/// Controlla cosa serve a SVNAssist (svn, client nativo, provider AI) e dice cosa manca e come installarlo.
/// </summary>
/// <remarks>
/// La logica di "quali provider sono utilizzabili" ricalca <see cref="AiServiceFactory"/>,
/// così la checklist non promette provider che la factory poi non userebbe.
/// </remarks>
internal sealed class PrerequisitesService
{
    internal const string TortoiseDownloadUrl = "https://tortoisesvn.net/downloads.html";
    internal const string ClaudeCodeDocsUrl = "https://docs.claude.com/en/docs/claude-code/overview";

    private const string GroupRequired = "Obbligatori";
    private const string GroupOptional = "Opzionali";
    private const string GroupAi = "Provider AI (ne basta uno)";

    private readonly ISvnAssistSettings _settings;
    private readonly PrerequisiteProbes _probes;

    public PrerequisitesService(ISvnAssistSettings settings, PrerequisiteProbes probes)
    {
        _settings = settings;
        _probes = probes;
    }

    /// <summary>Esegue tutte le verifiche.</summary>
    public async Task<PrerequisitesSnapshot> CheckAsync(CancellationToken ct)
    {
        var provider = (await _settings.GetAiProviderAsync(ct)).Trim().ToLowerInvariant();
        if (provider.Length == 0)
            provider = "auto";

        var svn = _probes.FindSvn();
        var tortoise = _probes.FindTortoise();
        var gh = _probes.FindGitHubCli();
        var claude = _probes.FindClaude();
        var ghLoggedIn = gh is not null && await _probes.IsGitHubCliLoggedIn(gh, ct);
        var token = await _probes.ResolveGitHubToken(ct);
        var copilotInstalled = _probes.CopilotRuntimeInstalled();

        var settingsKey = await _settings.GetAnthropicApiKeyAsync(ct);
        var envKey = _probes.ReadEnvironment("ANTHROPIC_API_KEY");
        var anthropicKeySource = !string.IsNullOrWhiteSpace(settingsKey) ? "Tools > Options"
            : !string.IsNullOrWhiteSpace(envKey) ? "variabile ANTHROPIC_API_KEY" : null;

        var apiKey = await _settings.GetAiApiKeyAsync(ct);
        var endpoint = await _settings.GetAiEndpointAsync(ct);

        var usable = new List<string>();
        bool Use(string name) => provider == name || provider == "auto";
        if (provider == "copilot" || (Use("copilot") && copilotInstalled && token is not null))
            usable.Add("GitHub Copilot");
        if (Use("claudecode") && claude is not null)
            usable.Add("Claude Code");
        if (Use("claude") && anthropicKeySource is not null)
            usable.Add("Claude API");
        if (Use("openai") && (!string.IsNullOrWhiteSpace(apiKey) || (AiServiceFactory.IsGitHubModelsEndpoint(endpoint) && token is not null)))
            usable.Add("OpenAI-compatibile");

        var items = new List<PrerequisiteItem>
        {
            new(PrerequisiteIds.Svn, GroupRequired, "Client Subversion (svn.exe)", svn is not null,
                svn ?? "svn.exe non trovato: serve per tutte le operazioni SVN.",
                "Si apre una finestra con:  winget install --id Slik.Subversion -e\n" +
                "Oppure installa TortoiseSVN e, nell'installer, abilita \"command line client tools\".\n" +
                "Poi premi Ricontrolla (se svn non viene trovato, riavvia Visual Studio).",
                "Installa con winget", "Pagina download"),

            new(PrerequisiteIds.TortoiseSvn, GroupOptional, "TortoiseSVN (diff nativo)", tortoise is not null,
                tortoise ?? "Opzionale: senza, il diff si vede nel pannello inline.",
                $"Scarica TortoiseSVN da {TortoiseDownloadUrl}\nNell'installer abilita anche \"command line client tools\".",
                "Pagina download"),

            new(PrerequisiteIds.GitHubCli, GroupAi, "GitHub CLI (gh)", gh is not null,
                gh ?? "gh.exe non trovato (per Copilot e GitHub Models).",
                "Si apre una finestra con:  winget install --id GitHub.cli -e\n" +
                "Poi premi Ricontrolla. Se gh non viene trovato, riavvia Visual Studio (il PATH non si aggiorna da solo).",
                gh is null ? "Installa GitHub CLI" : null),

            new(PrerequisiteIds.GitHubLogin, GroupAi, "GitHub CLI: login", ghLoggedIn,
                gh is null ? "Installa prima GitHub CLI."
                    : ghLoggedIn ? "login effettuato" : "non hai ancora fatto login.",
                "Si apre una finestra con:  gh auth login --hostname github.com --git-protocol https --web\n" +
                "Conferma il codice nel browser, poi premi Ricontrolla.",
                gh is not null && !ghLoggedIn ? "Accedi a GitHub" : null),

            new(PrerequisiteIds.ClaudeCode, GroupAi, "Claude Code", claude is not null,
                claude is null ? "claude.exe non trovato."
                    : $"{claude} (il login non è verificabile: premi \"Accedi a Claude Code\" se non l'hai mai fatto)",
                "Si apre una finestra PowerShell con:  irm https://claude.ai/install.ps1 | iex\n" +
                "Poi premi Ricontrolla ed esegui \"Accedi a Claude Code\" (nella finestra che si apre, /login con il tuo abbonamento).",
                claude is null ? "Installa Claude Code" : "Accedi a Claude Code",
                claude is null ? "Pagina documentazione" : null),

            new(PrerequisiteIds.AnthropicKey, GroupAi, "Anthropic API Key", anthropicKeySource is not null,
                anthropicKeySource is null ? "nessuna chiave" : $"chiave disponibile ({anthropicKeySource})",
                "Incolla la chiave in Tools > Options > SVNAssist > Anthropic API Key,\n" +
                "oppure imposta la variabile d'ambiente ANTHROPIC_API_KEY e riavvia Visual Studio."),

            new(PrerequisiteIds.CopilotRuntime, GroupAi, "Runtime GitHub Copilot (~100 MB)", copilotInstalled,
                copilotInstalled ? "scaricato" : "si scarica da solo al primo uso con AI Provider = copilot.",
                "Premi \"Scarica ora\" oppure imposta AI Provider = copilot in Tools > Options > SVNAssist.\n" +
                "Richiede anche il login a GitHub (gh auth login).",
                copilotInstalled ? null : "Scarica ora (~100 MB)"),
        };

        return new PrerequisitesSnapshot(items, provider, usable);
    }
}
