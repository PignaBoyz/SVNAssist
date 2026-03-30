using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;
using SVNAssist.Services;

namespace SVNAssist.Commands;

/// <summary>
/// Comando "Update" — esegue <c>svn update</c> sulla working copy corrente.
/// </summary>
/// <remarks>
/// Il percorso della working copy viene auto-rilevato dalla directory della solution
/// aperta in Visual Studio, usando la Project System Query API.
/// L'alternativa sarebbe chiedere il percorso all'utente, ma auto-rilevarlo
/// è più comodo per l'uso quotidiano.
///
/// Se nessuna solution è aperta, viene mostrato un messaggio di errore.
/// </remarks>
[VisualStudioContribution]
internal class UpdateCommand : Command
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.UpdateCommand.DisplayName%")
    {
    };

    /// <summary>
    /// Eseguito quando l'utente clicca "Update" nel menu.
    /// Auto-rileva la directory della solution e lancia <c>svn update</c>.
    /// </summary>
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Auto-rileva la directory della solution aperta in VS
            var solutionDir = await GetSolutionDirectoryAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(solutionDir))
            {
                await this.Extensibility.Shell().ShowPromptAsync(
                    "Nessuna solution aperta. Apri una solution in una working copy SVN.",
                    PromptOptions.OK,
                    cancellationToken);
                return;
            }

            var svnService = new SvnService();
            await svnService.UpdateAsync(solutionDir, cancellationToken);

            await this.Extensibility.Shell().ShowPromptAsync(
                $"Update completato per:\n{solutionDir}",
                PromptOptions.OK,
                cancellationToken);
        }
        catch (Exception ex)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Errore durante l'update:\n{ex.Message}",
                PromptOptions.OK,
                cancellationToken);
        }
    }

    /// <summary>
    /// Ottiene la directory della solution corrente usando la Project System Query API.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkspacesExtensibility.QuerySolutionAsync{T}"/> esegue una query
    /// sulla solution corrente di VS. Il metodo <c>With</c> specifica quali proprietà
    /// caricare — qui ci serve solo <see cref="ISolutionSnapshot.Directory"/>.
    /// L'alternativa sarebbe usare DTE o IVsSolution del vecchio modello,
    /// ma la Query API è il meccanismo ufficiale per le estensioni out-of-process.
    /// </remarks>
    private async Task<string?> GetSolutionDirectoryAsync(CancellationToken ct)
    {
        var result = await this.Extensibility.Workspaces().QuerySolutionAsync(
            solution => solution.With(s => s.Directory),
            ct);

        return result.FirstOrDefault()?.Directory;
    }
}
