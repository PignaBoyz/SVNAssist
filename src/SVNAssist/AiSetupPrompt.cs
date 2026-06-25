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
    // Pagina di creazione di un token "classic" senza scope: sufficiente per GitHub Models.
    private const string TokenPageUrl = "https://github.com/settings/tokens/new?description=SVNAssist&scopes=";

    private readonly VisualStudioExtensibility _extensibility;
    private int _alreadyPrompted;

    public AiSetupPrompt(VisualStudioExtensibility extensibility)
    {
        _extensibility = extensibility;
    }

    /// <inheritdoc />
    public async Task ShowMissingTokenHelpAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _alreadyPrompted, 1) == 1)
            return;

        var openPage = await _extensibility.Shell().ShowPromptAsync(
            "Per generare i commit con l'AI serve un token GitHub gratuito (una volta sola).\n\n" +
            "Vuoi aprire la pagina per creare un Personal Access Token? " +
            "Poi incollalo in Tools > Options > SVNAssist > AI API Key.\n\n" +
            "(Se usi GitHub CLI con 'gh auth login', non devi fare nulla: il token viene rilevato in automatico.)",
            PromptOptions.OKCancel,
            ct);

        if (!openPage)
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = TokenPageUrl, UseShellExecute = true });
        }
        catch
        {
            // Best-effort: se non riusciamo ad aprire il browser, non blocchiamo l'estensione.
        }
    }
}
