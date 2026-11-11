using SVNAssist.Models;
using SVNAssist.Rpc;
using SVNAssist.Services;

namespace SVNAssist.Hosting;

/// <summary>
/// <see cref="IDialogService"/> basato sui dialoghi nativi dell'host (<see cref="IHost.PromptAsync"/>).
/// </summary>
internal sealed class HostDialogs : IDialogService
{
    private static readonly string[] OkCancel = ["OK", "Annulla"];

    private readonly IHost _host;

    public HostDialogs(IHost host) => _host = host;

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string message, CancellationToken ct)
        => await _host.PromptAsync(message, OkCancel) == 0;

    /// <inheritdoc />
    public async Task<SvnConflictChoice?> ChooseConflictResolutionAsync(string fileName, CancellationToken ct)
    {
        // Una scelta per pulsante, con il significato dell'opzione di svn resolve --accept.
        var pick = await _host.PromptAsync(
            $"Come vuoi risolvere il conflitto su \"{fileName}\"?\n\n" +
            "• Ho risolto a mano: tieni il file com'è (sistemato a mano, senza marcatori <<<<<<<)\n" +
            "• Tieni il mio file: scarta le modifiche del server, vale il tuo file intero\n" +
            "• Prendi quello del server: scarta le TUE modifiche locali\n" +
            "• Torna alla base: scarta le modifiche di entrambi",
            ["Ho risolto a mano", "Tieni il mio file", "Prendi quello del server", "Torna alla base", "Annulla"]);

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
/// <see cref="IAiSetupPrompt"/> basato sull'host: prima scelta il login nel terminale (<c>gh auth login --web</c>, o
/// <c>claude</c> per Claude Code); annullando si passa all'alternativa (pagina per creare il token / la chiave).
/// Nessun flag "gia' mostrato": ogni click su "Genera con AI" senza provider pronto rimostra la guida.
/// </summary>
internal sealed class HostAiSetupPrompt : IAiSetupPrompt
{
    private static readonly string[] OkCancel = ["OK", "Annulla"];

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
