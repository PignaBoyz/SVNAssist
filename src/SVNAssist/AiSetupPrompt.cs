using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using SVNAssist.Services;

namespace SVNAssist;

/// <summary>
/// Implementazione di <see cref="IAiSetupPrompt"/> basata sull'API Shell di Visual Studio.
/// </summary>
/// <remarks>
/// Prima scelta (default): login nel terminale (<c>gh auth login --web</c>, o <c>claude</c> per Claude Code).
/// Annullando si passa all'alternativa: la pagina per creare il token / la chiave del provider.
/// Nessun flag "già mostrato": ogni click su "Genera con AI" senza provider pronto rimostra la guida
/// (un flag impostato prima della risposta e mai azzerato faceva tacere l'estensione dopo il primo popup chiuso).
/// </remarks>
internal sealed class AiSetupPrompt : IAiSetupPrompt
{
    private readonly VisualStudioExtensibility _extensibility;
    private readonly ISvnAssistSettings _settings;

    public AiSetupPrompt(VisualStudioExtensibility extensibility, ISvnAssistSettings settings)
    {
        _extensibility = extensibility;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<bool> ShowMissingTokenHelpAsync(CancellationToken ct)
    {
        var provider = await _settings.GetAiProviderAsync(ct);

        if (AiProviderGuide.LoginCommand(provider) is { } login)
        {
            var useTerminal = await _extensibility.Shell().ShowPromptAsync(
                AiProviderGuide.LoginPrompt(provider), PromptOptions.OKCancel, ct);
            if (useTerminal && LaunchLogin(login))
                return true;
        }

        var openPage = await _extensibility.Shell().ShowPromptAsync(
            AiProviderGuide.MissingProviderPrompt(provider), PromptOptions.OKCancel, ct);
        if (!openPage)
            return false;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = AiProviderGuide.SetupUrl(provider), UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lancia il login nel terminale; se gh/claude non sono installati lancia prima l'installazione.</summary>
    private static bool LaunchLogin(ConsoleCommand login)
    {
        var claude = ClaudeCodeAiService.FindClaudeExecutable();
        return login switch
        {
            ConsoleCommand.GitHubLogin when SvnLocator.FindExecutable("gh.exe") is null => ConsoleLauncher.Launch(ConsoleCommand.InstallGitHubCli),
            ConsoleCommand.ClaudeLogin when claude is null => ConsoleLauncher.Launch(ConsoleCommand.InstallClaudeCode),
            _ => ConsoleLauncher.Launch(login, claude),
        };
    }
}
