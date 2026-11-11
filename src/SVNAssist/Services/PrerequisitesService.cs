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
    public const string OpenAiKey = "openai-key";
}

/// <summary>
/// Cosa serve a ciascun provider AI (nota su requisiti e costi) e la pagina da aprire per configurarlo.
/// Condiviso da checklist dei prerequisiti e dal prompt "nessun provider AI".
/// </summary>
internal static class AiProviderGuide
{
    /// <summary>Pagina di creazione di un token GitHub "classic" senza scope: sufficiente per GitHub Models.</summary>
    internal const string GitHubTokenUrl = "https://github.com/settings/tokens/new?description=SVNAssist&scopes=";
    internal const string CopilotUrl = "https://github.com/features/copilot";
    internal const string AnthropicKeysUrl = "https://console.anthropic.com/settings/keys";

    /// <summary>Normalizza il valore dell'impostazione provider (vuoto = auto).</summary>
    internal static string Normalize(string? provider)
    {
        var p = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return p.Length == 0 ? "auto" : p;
    }

    /// <summary>Requisiti, login e costi del provider scelto, in una frase.</summary>
    internal static string Requirements(string provider) => Normalize(provider) switch
    {
        "copilot" => "GitHub Copilot: serve un abbonamento Copilot (le richieste possono consumare \"premium request\"), " +
                     "GitHub CLI con login (gh auth login) e il runtime Copilot (~100 MB, scaricato al primo uso).",
        "claudecode" => "Claude Code: serve Claude Code installato e il login (/login) con un abbonamento Claude Pro/Max; " +
                        "usa la quota del tuo abbonamento.",
        "claude" => "Claude API: serve una API key Anthropic; l'uso è a pagamento a consumo " +
                    "(Haiku costa meno di Sonnet, Sonnet meno di Opus).",
        "openai" => "OpenAI-compatibile: con GitHub Models (default) basta il login GitHub (gh auth login) o un token, gratis entro i limiti; " +
                    "con altri endpoint serve la loro API key (a pagamento, tranne Ollama in locale).",
        _ => "Automatico: prova in ordine Copilot, Claude Code, Claude API, OpenAI-compatibile; ne basta uno configurato.",
    };

    /// <summary>Pagina utile per configurare il provider scelto.</summary>
    internal static string SetupUrl(string provider) => Normalize(provider) switch
    {
        "copilot" => CopilotUrl,
        "claudecode" => PrerequisitesService.ClaudeCodeDocsUrl,
        "claude" => AnthropicKeysUrl,
        _ => GitHubTokenUrl,
    };

    /// <summary>Domanda mostrata quando nessun provider è utilizzabile, specifica per il provider scelto.</summary>
    internal static string MissingProviderPrompt(string provider) => Normalize(provider) switch
    {
        "copilot" => "GitHub Copilot non è utilizzabile.\n\n" + Requirements("copilot") + "\n\nVuoi aprire la pagina di GitHub Copilot?",
        "claudecode" => "Claude Code non è utilizzabile.\n\n" + Requirements("claudecode") + "\n\nVuoi aprire la documentazione di Claude Code?",
        "claude" => "Claude API non è utilizzabile.\n\n" + Requirements("claude") + "\nInserisci la chiave in Tools > Options > SVNAssist > Anthropic API Key.\n\nVuoi aprire la pagina per creare la API key Anthropic?",
        "openai" => "Il provider OpenAI-compatibile non è utilizzabile.\n\n" + Requirements("openai") + "\nIncolla la chiave in Tools > Options > SVNAssist > AI API Key.\n\nVuoi aprire la pagina per creare il token GitHub gratuito?",
        _ => "Nessun provider AI trovato per generare i commit. Basta UNA di queste opzioni:\n\n" +
             "• GitHub Copilot o GitHub CLI: esegui 'gh auth login'\n" +
             "• Claude: installa Claude Code e fai login, oppure inserisci una Anthropic API Key\n" +
             "• Gratis: crea un token GitHub e incollalo in Tools > Options > SVNAssist > AI API Key\n\n" +
             "Vuoi aprire la pagina per creare il token GitHub gratuito?",
    };
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
/// <param name="ProviderNote">Cosa serve al provider scelto (CLI, login, API key, costi).</param>
internal sealed record PrerequisitesSnapshot(
    IReadOnlyList<PrerequisiteItem> Items,
    string ConfiguredProvider,
    IReadOnlyList<string> UsableProviders,
    string ProviderNote = "")
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
                var result = await processRunner.RunWithTimeoutAsync(
                    gh, ["auth", "status"], null, GitHubTokenResolver.GhTimeout, "gh auth status", ct);
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
        var provider = AiProviderGuide.Normalize(await _settings.GetAiProviderAsync(ct));

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
        // Copilot serve il login GitHub (token): senza, non è utilizzabile nemmeno se scelto esplicitamente.
        if (token is not null && (provider == "copilot" || (Use("copilot") && copilotInstalled)))
            usable.Add("GitHub Copilot");
        if (Use("claudecode") && claude is not null)
            usable.Add("Claude Code");
        if (Use("claude") && anthropicKeySource is not null)
            usable.Add("Claude API");
        var openAiKeyOk = !string.IsNullOrWhiteSpace(apiKey) || (AiServiceFactory.IsGitHubModelsEndpoint(endpoint) && token is not null);
        if (Use("openai") && openAiKeyOk)
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

            new(PrerequisiteIds.OpenAiKey, GroupAi, "API key / token (OpenAI-compatibile)", openAiKeyOk,
                !string.IsNullOrWhiteSpace(apiKey) ? "chiave in Tools > Options"
                    : openAiKeyOk ? "token GitHub rilevato (GitHub Models)" : "nessuna chiave né token GitHub.",
                "Con GitHub Models (default) basta il login GitHub (gh auth login) oppure un token da incollare in\n" +
                "Tools > Options > SVNAssist > AI API Key. Per OpenAI/Azure incolla la loro API key (a pagamento)."),
        };

        // Con un provider fisso mostra solo i prerequisiti AI che lo riguardano (in auto, tutti).
        var relevant = RelevantAiItems(provider);
        if (relevant is not null)
            items.RemoveAll(i => i.Group == GroupAi && !relevant.Contains(i.Id));

        return new PrerequisitesSnapshot(items, provider, usable, AiProviderGuide.Requirements(provider));
    }

    private static HashSet<string>? RelevantAiItems(string provider) => provider switch
    {
        "copilot" => [PrerequisiteIds.GitHubCli, PrerequisiteIds.GitHubLogin, PrerequisiteIds.CopilotRuntime],
        "claudecode" => [PrerequisiteIds.ClaudeCode],
        "claude" => [PrerequisiteIds.AnthropicKey],
        "openai" => [PrerequisiteIds.GitHubCli, PrerequisiteIds.GitHubLogin, PrerequisiteIds.OpenAiKey],
        _ => null,
    };
}
