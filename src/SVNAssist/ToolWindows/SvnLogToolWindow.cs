using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Tool window "SVN Log" — mostra la cronologia delle revisioni SVN
/// e il diff della revisione selezionata.
/// </summary>
/// <remarks>
/// Stessa architettura di <see cref="SvnStatusToolWindow"/>: come quella, rileva da sola la working copy
/// dalla solution aperta e carica le prime 50 revisioni; "Carica altre 50" estende la lista.
/// Placement <see cref="ToolWindowPlacement.DocumentWell"/>: appare nell'area dei documenti.
/// </remarks>
[VisualStudioContribution]
internal class SvnLogToolWindow : ToolWindow
{
    private readonly SvnLogToolWindowData _dataContext;

    public SvnLogToolWindow(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
        Title = "SVN Log";

        // svn viene ritrovato anche se installato dopo l'apertura di VS.
        _dataContext = new SvnLogToolWindowData(new ReloadableSvnService());
    }

    /// <inheritdoc />
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    /// <inheritdoc />
    public override async Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        // Auto-detect come nella finestra Status: se la solution è una working copy, il log parte da solo.
        try
        {
            var result = await Extensibility.Workspaces().QuerySolutionAsync(
                solution => solution.With(s => s.Directory),
                cancellationToken);

            var solutionDir = result.FirstOrDefault()?.Directory;
            var svnRoot = !string.IsNullOrWhiteSpace(solutionDir) ? SvnService.FindSvnRoot(solutionDir) : null;
            await _dataContext.ApplyAutoDetectedWorkingCopyAsync(svnRoot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Nessuna solution aperta: si può indicare il percorso a mano.
        }

        return new SvnLogToolWindowControl(_dataContext);
    }
}
