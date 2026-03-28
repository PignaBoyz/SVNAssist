using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Tool window "SVN Status" — mostra la lista dei file modificati nella working copy.
/// </summary>
/// <remarks>
/// Nel nuovo modello VisualStudio.Extensibility, una tool window:
/// - Eredita da <see cref="ToolWindow"/> (non da <c>ToolWindowPane</c> del vecchio modello)
/// - Usa <c>[VisualStudioContribution]</c> per registrarsi automaticamente
/// - Restituisce un <see cref="IRemoteUserControl"/> come contenuto (Remote UI)
/// - Gira out-of-process — il crash della tool window non fa crashare VS
///
/// <see cref="ToolWindowConfiguration"/> definisce come VS gestisce la finestra:
/// - Placement: dove appare di default (DocumentWell = area dei tab dei documenti)
/// - DockDirection: direzione di ancoraggio
///
/// Il parametro <see cref="Services.Settings.SvnAssistCategoryObserver"/> viene
/// iniettato dal container DI — registrato da <c>AddSettingsObservers()</c>
/// in <see cref="SVNAssistExtension.InitializeServices"/>.
/// </remarks>
[VisualStudioContribution]
internal class SvnStatusToolWindow : ToolWindow
{
    private readonly SvnStatusToolWindowData _dataContext;

    public SvnStatusToolWindow(
        VisualStudioExtensibility extensibility,
        Services.Settings.SvnAssistCategoryObserver settingsObserver)
        : base(extensibility)
    {
        Title = "SVN Status";
        var settingsService = new SettingsService(settingsObserver);
        _dataContext = new SvnStatusToolWindowData(extensibility, settingsService);
    }

    /// <inheritdoc />
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    /// <summary>
    /// Restituisce il controllo Remote UI da mostrare nella tool window.
    /// VS chiama questo metodo quando la finestra viene aperta per la prima volta.
    /// </summary>
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IRemoteUserControl>(
            new SvnStatusToolWindowControl(_dataContext));
    }
}
