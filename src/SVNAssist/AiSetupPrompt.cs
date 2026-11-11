using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using SVNAssist.Services;

namespace SVNAssist;

/// <summary>
/// Implementazione di <see cref="IAiSetupPrompt"/> basata sull'API Shell di Visual Studio.
/// </summary>
/// <remarks>
/// Per i commit message SVNAssist usa GitHub Models (gratuito), che richiede un token GitHub.
/// L'unico prerequisito è quindi un Personal Access Token (PAT) gratuito, da incollare una volta
/// in Tools &gt; Options. Chi ha GitHub CLI loggato non deve fare nemmeno questo (token rilevato
/// automaticamente da <see cref="GitHubTokenResolver"/>).
/// </remarks>
internal sealed class AiSetupPrompt : IAiSetupPrompt
{
    private readonly VisualStudioExtensibility _extensibility;
    private readonly ISvnAssistSettings _settings;
    private int _alreadyPrompted;

    public AiSetupPrompt(VisualStudioExtensibility extensibility, ISvnAssistSettings settings)
    {
        _extensibility = extensibility;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task ShowMissingTokenHelpAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _alreadyPrompted, 1) == 1)
            return;

        // Testo e pagina dipendono dal provider scelto, non solo dal token GitHub.
        var provider = await _settings.GetAiProviderAsync(ct);
        var openPage = await _extensibility.Shell().ShowPromptAsync(
            AiProviderGuide.MissingProviderPrompt(provider),
            PromptOptions.OKCancel,
            ct);

        if (!openPage)
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = AiProviderGuide.SetupUrl(provider), UseShellExecute = true });
        }
        catch
        {
            // Best-effort: se non riusciamo ad aprire il browser, non blocchiamo l'estensione.
        }
    }
}
