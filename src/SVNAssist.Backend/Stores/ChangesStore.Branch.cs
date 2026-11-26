using System.Collections.Concurrent;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Ramo collegato a un work item di Azure DevOps (file nascosto <c>.svnassist</c>, vedi <see cref="BranchLinks"/>):
/// collegamento alla creazione del ramo o dopo, avviso se la feature e' chiusa, proposta di chiusura al rientro completo del ramo.
/// Niente commit apposta: il file cambiato entra nel primo commit dell'utente.
/// </summary>
internal sealed partial class ChangesStore
{
    /// <summary>Collegamenti scelti creando un ramo senza esserci passati sopra: si scrivono quando la working copy arriva su quel ramo.</summary>
    private readonly ConcurrentDictionary<string, BranchLink> _pendingBranchLinks = new(StringComparer.Ordinal);

    private string CurrentBranchPath()
    {
        lock (_stateLock)
            return BranchLinks.PathOf(_branchUrl ?? string.Empty, _repoRoot ?? string.Empty);
    }

    /// <summary>Il file <c>.svnassist</c> della radice e' cambiato e non ancora committato.</summary>
    private ChangeEntry? BranchFileEntry()
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
            return null;
        var f = Path.Combine(wc, BranchLinks.FileName);
        lock (_stateLock)
            return _entries.FirstOrDefault(e => string.Equals(e.FullPath, f, StringComparison.OrdinalIgnoreCase)
                && e.Status is SvnStatusKind.Added or SvnStatusKind.Modified);
    }

    /// <summary>Dopo ogni refresh: collegamento del ramo corrente (e quello rimasto in attesa dalla creazione del ramo).</summary>
    private async Task RefreshBranchLinkAsync(CancellationToken ct)
    {
        try
        {
            if (!(await Azure.SettingsAsync(ct)).Ready)
                return;
            var wc = WorkingCopyPath;
            var path = CurrentBranchPath();
            if (string.IsNullOrWhiteSpace(wc) || path.Length == 0)
                return;
            var link = BranchLinks.For(BranchLinks.Read(wc), path);
            if (_pendingBranchLinks.TryRemove(path, out var wanted) && link.Role == BranchRole.None)
            {
                await WriteBranchLinkAsync(wanted, ct);
                return; // il refresh dopo la scrittura richiama questo metodo
            }

            await Azure.SetBranchAsync(link, BranchFileEntry() is not null, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Info("Collegamento del ramo non letto: " + ex.Message);
        }
    }

    /// <summary>Scrive la riga del ramo nel file (nascosto), lo aggiunge a svn se nuovo, poi refresh. Nessun commit.</summary>
    private async Task WriteBranchLinkAsync(BranchLink link, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        var isNew = BranchLinks.Write(wc, BranchLinks.With(BranchLinks.Read(wc), link));
        if (isNew)
            await _svnService.AddAsync([Path.Combine(wc, BranchLinks.FileName)], ct);
        await RefreshAsync(ct, reloadBranch: false, link.Role switch
        {
            BranchRole.WorkItem => Loc.T("Ramo collegato al work item {0}: il collegamento entra nel tuo prossimo commit.", link.WorkItem),
            BranchRole.Release => Loc.T("Ramo segnato come ramo di rilascio: entra nel tuo prossimo commit."),
            _ => Loc.T("Ramo scollegato: entra nel tuo prossimo commit."),
        });
    }

    /// <summary>Comando <c>azBranchSet</c>: role = workitem (con id) | release | none.</summary>
    private async Task SetBranchLinkAsync(string role, long id, CancellationToken ct)
    {
        var path = CurrentBranchPath();
        if (path.Length == 0 || string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            SetStatus(Loc.T("Nessun ramo da collegare: working copy non letta."));
            return;
        }

        BranchLink link;
        if (role == "workitem")
        {
            if (id is < 1 or > int.MaxValue)
                throw new ArgumentException(Loc.T("Numero di work item non valido."));
            if (await Azure.CheckPickAsync((int)id, ct) is null)
                return; // errore nel dialogo
            link = new BranchLink(path, BranchRole.WorkItem, (int)id);
        }
        else
            link = new BranchLink(path, role == "release" ? BranchRole.Release : BranchRole.None);

        Azure.ClosePick();
        await WriteBranchLinkAsync(link, ct);
    }

    /// <summary>Creazione di un ramo con il work item scelto: si scrive quando la working copy e' sul ramo (subito, se si passa al ramo).</summary>
    private void RememberNewBranchLink(string destUrl, long workItem)
    {
        if (workItem is < 1 or > int.MaxValue)
            return;
        string? root;
        lock (_stateLock)
            root = _repoRoot;
        var path = BranchLinks.PathOf(destUrl, root ?? string.Empty);
        if (path.Length > 0)
            _pendingBranchLinks[path] = new BranchLink(path, BranchRole.WorkItem, (int)workItem);
    }

    /// <summary>
    /// Dopo un merge concluso dell'intera working copy: se il ramo sorgente e' collegato a un work item, il rientro e' completo
    /// (svn mergeinfo: niente piu' da unire) e la destinazione e' il ramo da cui e' nato o un ramo di rilascio, propone la chiusura.
    /// Un merge di singole revisioni o verso un altro ramo non propone nulla.
    /// </summary>
    private async Task CheckMergeCloseAsync(string sourceUrl, string sourcePath, CancellationToken ct)
    {
        try
        {
            if (!(await Azure.SettingsAsync(ct)).Ready)
                return;
            var wc = WorkingCopyPath;
            var srcPath = "/" + sourcePath.Trim('/');
            var src = BranchLinks.For(await _svnService.CatAsync(sourceUrl.TrimEnd('/') + "/" + BranchLinks.FileName, ct), srcPath);
            if (src.Role != BranchRole.WorkItem)
                return;
            if ((await _svnService.GetEligibleRevisionsAsync(wc, sourceUrl, ct)).Count > 0)
                return; // merge di singole revisioni: il ramo non e' rientrato tutto
            var here = CurrentBranchPath();
            var origin = "/" + ((await _svnService.GetCopySourceAsync(sourceUrl, ct)) ?? string.Empty).Trim('/');
            var release = BranchLinks.For(BranchLinks.Read(wc), here).Role == BranchRole.Release;
            if (!string.Equals(origin, here, StringComparison.Ordinal) && !release)
                return;
            await Azure.ProposeMergeCloseAsync(src.WorkItem, srcPath.Split('/').Last(), here.Split('/').Last(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Info("Chiusura al rientro non verificata: " + ex.Message);
        }
    }
}
