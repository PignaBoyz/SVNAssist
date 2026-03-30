using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SVNAssist.ToolWindows;

namespace SVNAssist.Commands;

/// <summary>
/// Comando "Log / History" — apre la tool window con la cronologia SVN.
/// </summary>
[VisualStudioContribution]
internal class ShowLogCommand : Command
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.ShowLogCommand.DisplayName%")
    {
    };

    /// <summary>
    /// Eseguito quando l'utente clicca "Log / History" nel menu.
    /// Apre (o porta in primo piano) la tool window SVN Log.
    /// </summary>
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowToolWindowAsync<SvnLogToolWindow>(activate: true, cancellationToken);
    }
}
