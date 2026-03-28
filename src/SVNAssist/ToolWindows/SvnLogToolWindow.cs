using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Tool window "SVN Log" — mostra la cronologia delle revisioni SVN
/// e il diff della revisione selezionata.
/// </summary>
/// <remarks>
/// Stessa architettura di <see cref="SvnStatusToolWindow"/>:
/// - Registrata con <c>[VisualStudioContribution]</c>
/// - Restituisce un <see cref="IRemoteUserControl"/> via <see cref="GetContentAsync"/>
/// - Il data context (<see cref="SvnLogToolWindowData"/>) contiene tutta la logica
///
/// Placement configurato su <see cref="ToolWindowPlacement.DocumentWell"/>
/// — appare nell'area dei tab dei documenti, accanto al codice.
/// </remarks>
[VisualStudioContribution]
internal class SvnLogToolWindow : ToolWindow
{
    private readonly SvnLogToolWindowData _dataContext = new();

    public SvnLogToolWindow(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
        Title = "SVN Log";
    }

    /// <inheritdoc />
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    /// <inheritdoc />
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IRemoteUserControl>(
            new SvnLogToolWindowControl(_dataContext));
    }
}
