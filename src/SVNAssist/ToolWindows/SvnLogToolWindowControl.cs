using Microsoft.VisualStudio.Extensibility.UI;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Controllo Remote UI per la tool window SVN Log.
/// Collega il XAML (embedded resource) al data context (<see cref="SvnLogToolWindowData"/>).
/// </summary>
/// <remarks>
/// Stessa architettura di <see cref="SvnStatusToolWindowControl"/>:
/// il XAML viene trovato per convenzione (stesso namespace + stesso nome classe),
/// serializzato verso il processo VS, e i comandi tornano via JSON-RPC.
/// </remarks>
internal class SvnLogToolWindowControl : RemoteUserControl
{
    public SvnLogToolWindowControl(SvnLogToolWindowData dataContext)
        : base(dataContext: dataContext)
    {
    }
}
