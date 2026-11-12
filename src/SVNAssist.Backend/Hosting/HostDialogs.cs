using SVNAssist.Models;
using SVNAssist.Rpc;
using SVNAssist.Services;

namespace SVNAssist.Hosting;

/// <summary>
/// <see cref="IDialogService"/> basato sui dialoghi nativi dell'host (<see cref="IHost.PromptAsync"/>).
/// </summary>
internal sealed class HostDialogs : IDialogService
{
    private static string[] OkCancel => ["OK", Loc.T("Annulla")];

    private readonly IHost _host;

    public HostDialogs(IHost host) => _host = host;

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string message, CancellationToken ct)
        => await _host.PromptAsync(message, OkCancel) == 0;

    /// <inheritdoc />
    public Task<string?> AskTextAsync(string message, string label, string defaultValue, CancellationToken ct)
        => _host.PromptTextAsync(message, label, defaultValue);

    /// <inheritdoc />
    public async Task<SvnConflictChoice?> ChooseConflictResolutionAsync(string fileName, CancellationToken ct)
    {
        // Una scelta per pulsante, con il significato dell'opzione di svn resolve --accept.
        var pick = await _host.PromptAsync(
            Loc.T("Come vuoi risolvere il conflitto su \"{0}\"?\n\n" +
                  "• Ho risolto a mano: tieni il file com'è (sistemato a mano, senza marcatori <<<<<<<)\n" +
                  "• Tieni il mio file: scarta le modifiche del server, vale il tuo file intero\n" +
                  "• Prendi quello del server: scarta le TUE modifiche locali\n" +
                  "• Torna alla base: scarta le modifiche di entrambi", fileName),
            [Loc.T("Ho risolto a mano"), Loc.T("Tieni il mio file"), Loc.T("Prendi quello del server"), Loc.T("Torna alla base"), Loc.T("Annulla")]);

        return pick switch
        {
            0 => SvnConflictChoice.Working,
            1 => SvnConflictChoice.MineFull,
            2 => SvnConflictChoice.TheirsFull,
            3 => SvnConflictChoice.Base,
            _ => null,
        };
    }
}

/// <summary>
/// <see cref="ISvnCredentialPrompt"/> basato sull'host: dialogo utente/password nativo. Le credenziali passano solo da
/// qui al processo svn: nessun log, nessuno stato UI.
/// </summary>
internal sealed class HostCredentialPrompt : ISvnCredentialPrompt
{
    private readonly IHost _host;

    public HostCredentialPrompt(IHost host) => _host = host;

    public async Task<SvnCredentials?> RequestAsync(string detail, CancellationToken ct)
    {
        BackendLog.Info("Autenticazione SVN richiesta all'utente.");
        var r = await _host.PromptCredentialsAsync(detail, Environment.UserName);
        return r is { Length: >= 3 } && !string.IsNullOrEmpty(r[0])
            ? new SvnCredentials(r[0], r[1] ?? string.Empty, r[2] == "1")
            : null;
    }
}

/// <summary>
/// <see cref="IAiSetupPrompt"/> basato sull'host: prima scelta il login nel terminale (<c>gh auth login --web</c>, o
/// <c>claude</c> per Claude Code); annullando si passa all'alternativa (pagina per creare il token / la chiave).
/// Nessun flag "gia' mostrato": ogni click su "Genera con AI" senza provider pronto rimostra la guida.
/// </summary>
internal sealed class HostAiSetupPrompt : IAiSetupPrompt
{
    private static string[] OkCancel => ["OK", Loc.T("Annulla")];

    private readonly IHost _host;
    private readonly ISvnAssistSettings _settings;
    private readonly Func<string?> _findClaude;
    private readonly Func<string, bool> _hasExecutable;

    public HostAiSetupPrompt(IHost host, ISvnAssistSettings settings, Func<string?>? findClaude = null, Func<string, bool>? hasExecutable = null)
    {
        _host = host;
        _settings = settings;
        _findClaude = findClaude ?? ClaudeCodeAiService.FindClaudeExecutable;
        _hasExecutable = hasExecutable ?? (name => SvnLocator.FindExecutable(name) is not null);
    }

    /// <inheritdoc />
    public async Task<bool> ShowMissingTokenHelpAsync(CancellationToken ct)
    {
        var provider = await _settings.GetAiProviderAsync(ct);

        if (AiProviderGuide.LoginCommand(provider) is { } login)
        {
            var useTerminal = await _host.PromptAsync(AiProviderGuide.LoginPrompt(provider), OkCancel) == 0;
            if (useTerminal && await LaunchLoginAsync(login))
                return true;
        }

        var openPage = await _host.PromptAsync(AiProviderGuide.MissingProviderPrompt(provider), OkCancel) == 0;
        if (!openPage)
            return false;

        try
        {
            await _host.OpenUrlAsync(AiProviderGuide.SetupUrl(provider));
            return true;
        }
        catch (Exception ex)
        {
            BackendLog.Warn("OpenUrlAsync fallita: " + ex.Message);
            return false;
        }
    }

    /// <summary>Lancia il login nel terminale; se gh/claude non sono installati lancia prima l'installazione.</summary>
    private async Task<bool> LaunchLoginAsync(ConsoleCommand login)
    {
        var claude = _findClaude();
        var (command, claudePath) = login switch
        {
            ConsoleCommand.GitHubLogin when !_hasExecutable("gh.exe") => (ConsoleCommand.InstallGitHubCli, null),
            ConsoleCommand.ClaudeLogin when claude is null => (ConsoleCommand.InstallClaudeCode, null),
            _ => (login, claude),
        };

        try
        {
            await _host.RunInTerminalAsync(ConsoleLauncher.ShellCommand(command, claudePath), Environment.CurrentDirectory);
            return true;
        }
        catch (Exception ex)
        {
            BackendLog.Warn("RunInTerminalAsync fallita: " + ex.Message);
            return false;
        }
    }
}
