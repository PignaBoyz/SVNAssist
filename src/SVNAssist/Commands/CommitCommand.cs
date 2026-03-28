using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SVNAssist.ToolWindows;

namespace SVNAssist.Commands;

/// <summary>
/// Comando "Commit..." — apre la tool window Status per selezionare i file
/// e procedere con il commit tramite il CommitDialog.
/// </summary>
/// <remarks>
/// Il flusso completo del commit passa dalla tool window Status:
/// 1. L'utente seleziona i file con le checkbox
/// 2. Clicca "Commit selezionati" → si apre il CommitDialog
/// 3. Scrive il messaggio (o lo genera con AI) → clicca OK
///
/// Questo comando è quindi un shortcut per aprire la tool window Status,
/// identico a <see cref="StatusCommand"/> ma con un nome più esplicito nel menu.
/// </remarks>
[VisualStudioContribution]
internal class CommitCommand : Command
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.CommitCommand.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    };

    /// <summary>
    /// Eseguito quando l'utente clicca "Commit..." nel menu.
    /// Apre la tool window SVN Status dove l'utente può selezionare file e committare.
    /// </summary>
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowToolWindowAsync<SvnStatusToolWindow>(activate: true, cancellationToken);
    }
}
