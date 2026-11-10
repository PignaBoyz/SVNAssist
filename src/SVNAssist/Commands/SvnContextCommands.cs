using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.ToolWindows;

namespace SVNAssist.Commands;

// Comandi del sottomenu "SVN" nel context menu di Solution Explorer (vedi SolutionExplorerMenu).
// Ogni classe è minuscola: la logica condivisa sta in SvnContextCommand e SvnTarget.

/// <summary>"SVN Update" sull'elemento cliccato.</summary>
[VisualStudioContribution]
internal sealed class SeUpdateCommand : SvnContextCommand
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeUpdateCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore durante l'update";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct)
    {
        // Un file si aggiorna da solo (svn update <file>), una cartella per intero.
        var result = await svn.UpdateAsync(target.Path, ct);

        var summary = result.Revision is { } revision ? $"Aggiornato alla revisione {revision}" : "Update completato";
        if (result.ConflictedPaths.Count > 0)
            summary += $"\n\n{result.ConflictedPaths.Count} elementi in conflitto:\n{SvnTarget.FormatList(result.ConflictedPaths)}";

        await ShowAsync($"{summary}\n\n{target.Path}", ct);
    }
}

/// <summary>"SVN Commit..." — apre la finestra SVN Changes.</summary>
[VisualStudioContribution]
internal sealed class SeCommitCommand : SvnContextCommand
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeCommitCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore nell'apertura di SVN Changes";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct) =>
        await this.Extensibility.Shell().ShowToolWindowAsync<SvnStatusToolWindow>(activate: true, ct);
}

/// <summary>"Diff con BASE" — diff nativo (TortoiseSVN) per i file, testo del diff in un prompt come ripiego.</summary>
[VisualStudioContribution]
internal sealed class SeDiffCommand : SvnContextCommand
{
    private const int MaxPromptChars = 4000;

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeDiffCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore durante il diff";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct)
    {
        if (!target.IsDirectory && await svn.OpenNativeDiffAsync(target.Path, ct))
            return;

        var diff = await svn.GetFileDiffAsync(target.Path, ct);
        if (string.IsNullOrWhiteSpace(diff))
        {
            await ShowAsync($"Nessuna modifica rispetto a BASE:\n{target.Path}", ct);
            return;
        }

        if (diff.Length > MaxPromptChars)
            diff = diff[..MaxPromptChars] + "\n... (diff troncato: usa SVN Changes o TortoiseSVN per vederlo per intero)";

        await ShowAsync(diff, ct);
    }
}

/// <summary>"Mostra log" — apre la finestra SVN Log (non accetta ancora un percorso).</summary>
[VisualStudioContribution]
internal sealed class SeLogCommand : SvnContextCommand
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeLogCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore nell'apertura del log";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct) =>
        await this.Extensibility.Shell().ShowToolWindowAsync<SvnLogToolWindow>(activate: true, ct);
}

/// <summary>"SVN Add" — aggiunge il file, o la cartella con il contenuto non versionato (ricorsivo).</summary>
[VisualStudioContribution]
internal sealed class SeAddCommand : SvnContextCommand
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeAddCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore durante l'add";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct)
    {
        // "svn add" su una cartella/progetto/solution è ricorsivo e prenderebbe anche bin/, obj/, .vs/, packages/:
        // si elencano prima gli elementi non versionati e si chiede conferma (come per il revert).
        if (target.IsDirectory)
        {
            var unversioned = (await svn.GetStatusAsync(target.Path, ct))
                .Where(c => c.Status == SvnStatusKind.Unversioned).Select(c => c.Path).ToList();
            if (unversioned.Count == 0)
            {
                await ShowAsync($"Nessun elemento non versionato da aggiungere in:\n{target.Path}", ct);
                return;
            }

            if (!await ConfirmAsync(
                    $"Aggiungere a SVN {unversioned.Count} elementi non versionati (con tutto il loro contenuto) in\n" +
                    $"{target.Path}\n\n{SvnTarget.FormatList(unversioned)}\n\n" +
                    "Attenzione: cartelle come bin/obj/.vs/packages vanno ignorate, non aggiunte.", ct))
                return;
        }

        await svn.AddAsync([target.Path], ct);
        await ShowAsync($"Aggiunto a SVN:\n{target.Path}", ct);
    }
}

/// <summary>"SVN Revert" — annulla le modifiche locali, sempre dopo conferma esplicita.</summary>
[VisualStudioContribution]
internal sealed class SeRevertCommand : SvnContextCommand
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeRevertCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore durante il revert";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct)
    {
        string question;
        if (target.IsDirectory)
        {
            var changes = await svn.GetStatusAsync(target.Path, ct);
            if (changes.Count == 0)
            {
                await ShowAsync($"Nessuna modifica da annullare in:\n{target.Path}", ct);
                return;
            }

            var names = changes.Select(c => $"{c.Status}  {c.Path}").ToList();
            question = $"Annullare le modifiche locali di {changes.Count} elementi in\n{target.Path}\n\n{SvnTarget.FormatList(names)}";
        }
        else
        {
            question = $"Annullare le modifiche locali di:\n{target.Path}";
        }

        // Distruttivo e irreversibile: mai senza conferma.
        if (!await ConfirmAsync(question + "\n\nL'operazione non è reversibile.", ct))
            return;

        await svn.RevertAsync([target.Path], ct);
        await ShowAsync($"Revert completato:\n{target.Path}", ct);
    }
}

/// <summary>"Aggiungi a ignore list" — aggiunge il nome a svn:ignore della cartella padre.</summary>
[VisualStudioContribution]
internal sealed class SeIgnoreCommand : SvnContextCommand
{
    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%SVNAssist.SeIgnoreCommand.DisplayName%");

    /// <inheritdoc />
    protected override string ErrorTitle => "Errore durante l'aggiunta alla ignore list";

    /// <inheritdoc />
    protected override async Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct)
    {
        await svn.AddToIgnoreListAsync(target.Path, ct);
        await ShowAsync($"Aggiunto a svn:ignore:\n{target.Path}\n\nLa modifica alla proprietà va committata sulla cartella padre.", ct);
    }
}
