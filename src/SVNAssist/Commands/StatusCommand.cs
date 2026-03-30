using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SVNAssist.ToolWindows;

namespace SVNAssist.Commands;

/// <summary>
/// Comando "Mostra Status" — apre la tool window con lo stato dei file SVN.
/// </summary>
/// <remarks>
/// Nel nuovo modello, un comando è semplicemente una classe che eredita da <see cref="Command"/>.
/// L'attributo <c>[VisualStudioContribution]</c> dice al source generator di registrarlo
/// automaticamente — non serve codice di inizializzazione manuale.
///
/// <see cref="CommandConfiguration"/> definisce come il comando appare nell'IDE:
/// - Il testo del bottone
/// - Dove viene posizionato (in quale menu)
/// - Eventuali icone e shortcut
/// </remarks>
[VisualStudioContribution]
internal class StatusCommand : Command
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.StatusCommand.DisplayName%")
    {
    };

    /// <summary>
    /// Eseguito quando l'utente clicca "Mostra Status" nel menu.
    /// </summary>
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        // Apre (o porta in primo piano) la tool window SVN Status.
        // ShowToolWindowAsync è il metodo del nuovo modello — nel vecchio
        // servivano FindToolWindow + IVsWindowFrame + Show().
        await this.Extensibility.Shell().ShowToolWindowAsync<SvnStatusToolWindow>(
            activate: true, cancellationToken);
    }
}
