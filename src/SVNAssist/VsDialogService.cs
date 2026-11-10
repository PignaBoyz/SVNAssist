using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist;

/// <summary>
/// Implementazione di <see cref="IDialogService"/> basata sull'API Shell di Visual Studio
/// (<c>ShowPromptAsync</c>): dialoghi nativi, tema compreso.
/// </summary>
internal sealed class VsDialogService : IDialogService
{
    // PromptOptions<T> richiede un tipo valore non nullable: "Annulla" è quindi un valore dedicato.
    private enum ConflictPick
    {
        Working,
        MineFull,
        TheirsFull,
        Base,
        Cancel,
    }

    private readonly VisualStudioExtensibility _extensibility;

    public VsDialogService(VisualStudioExtensibility extensibility)
    {
        _extensibility = extensibility;
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string message, CancellationToken ct)
        => _extensibility.Shell().ShowPromptAsync(message, PromptOptions.OKCancel, ct);

    /// <inheritdoc />
    public async Task<SvnConflictChoice?> ChooseConflictResolutionAsync(string fileName, CancellationToken ct)
    {
        // Una scelta per bottone, con il significato dell'opzione di svn resolve --accept.
        var choices = new ChoiceResultCollection<ConflictPick>
        {
            { "Ho risolto a mano", ConflictPick.Working },
            { "Tieni il mio file", ConflictPick.MineFull },
            { "Prendi quello del server", ConflictPick.TheirsFull },
            { "Torna alla base", ConflictPick.Base },
            { "Annulla", ConflictPick.Cancel },
        };

        var options = new PromptOptions<ConflictPick>(choices, defaultChoiceIndex: 0, dismissedReturns: ConflictPick.Cancel);

        var pick = await _extensibility.Shell().ShowPromptAsync(
            $"Come vuoi risolvere il conflitto su \"{fileName}\"?\n\n" +
            "• Ho risolto a mano: tieni il file com'è (sistemato a mano, senza marcatori <<<<<<<)\n" +
            "• Tieni il mio file: scarta le modifiche del server, vale il tuo file intero\n" +
            "• Prendi quello del server: scarta le TUE modifiche locali\n" +
            "• Torna alla base: scarta le modifiche di entrambi",
            options,
            ct);

        return pick switch
        {
            ConflictPick.Working => SvnConflictChoice.Working,
            ConflictPick.MineFull => SvnConflictChoice.MineFull,
            ConflictPick.TheirsFull => SvnConflictChoice.TheirsFull,
            ConflictPick.Base => SvnConflictChoice.Base,
            _ => null,
        };
    }
}
