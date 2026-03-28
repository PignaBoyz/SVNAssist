using Microsoft.VisualStudio.Extensibility.UI;

namespace SVNAssist.Dialogs;

/// <summary>
/// Controllo Remote UI per il dialog di commit.
/// Collega il XAML (embedded resource) al data context (<see cref="CommitDialogData"/>).
/// </summary>
/// <remarks>
/// Funziona come <see cref="SVNAssist.ToolWindows.SvnStatusToolWindowControl"/>:
/// il XAML viene trovato per convenzione (stesso namespace + stesso nome classe),
/// e il <paramref name="dataContext"/> viene serializzato verso il processo VS.
///
/// Questo controllo viene passato a <c>Shell().ShowDialogAsync()</c> che lo
/// renderizza in una finestra modale con bottoni OK/Cancel gestiti da VS.
/// </remarks>
internal class CommitDialogControl : RemoteUserControl
{
    public CommitDialogControl(CommitDialogData dataContext)
        : base(dataContext: dataContext)
    {
    }
}
