using Microsoft.VisualStudio.Extensibility.UI;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Controllo Remote UI per la tool window SVN Status.
/// Collega il file XAML (embedded resource) al data context (<see cref="SvnStatusToolWindowData"/>).
/// </summary>
/// <remarks>
/// <see cref="RemoteUserControl"/> è il meccanismo di VisualStudio.Extensibility per
/// mostrare UI all'interno di Visual Studio da un'estensione out-of-process.
///
/// Come funziona:
/// 1. Il XAML viene trovato per convenzione: stesso nome della classe, stessa cartella
/// 2. Il XAML viene inviato al processo VS che lo renderizza
/// 3. I dati dal <paramref name="dataContext"/> vengono serializzati e sincronizzati
///    tra i due processi tramite un protocollo basato su JSON-RPC
/// 4. Quando l'utente interagisce (click, checkbox, ecc.), gli eventi tornano
///    al processo dell'estensione via lo stesso protocollo
/// </remarks>
internal class SvnStatusToolWindowControl : RemoteUserControl
{
    public SvnStatusToolWindowControl(SvnStatusToolWindowData dataContext)
        : base(dataContext: dataContext)
    {
    }
}
