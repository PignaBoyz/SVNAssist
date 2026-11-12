using SVNAssist.Models;
using SVNAssist.Rpc;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Comandi del menu contestuale di Solution Explorer (<see cref="IBackend.RunPathCommandAsync"/>): agiscono direttamente
/// sui percorsi selezionati (.sln/.csproj = la loro cartella, via <see cref="SvnTarget"/>), con conferme e esito
/// mostrati con i dialoghi dell'host. Gli errori diventano un dialogo, mai un'eccezione verso l'host.
/// </summary>
internal sealed class PathCommands
{
    private readonly ISvnService _svn;
    private readonly IHost _host;
    private readonly Func<Task> _refresh;
    private readonly DiffOpener _diffs;

    public PathCommands(ISvnService svn, IHost host, Func<Task> refresh, string tempRoot)
    {
        _svn = svn;
        _host = host;
        _refresh = refresh;
        _diffs = new DiffOpener(svn, host, tempRoot);
    }

    /// <summary>Cancella i file temporanei dei diff aperti (chiusura del backend).</summary>
    internal void CleanTemp() => _diffs.Dispose();

    public async Task RunAsync(string command, string[] paths, CancellationToken ct)
    {
        // commit: l'host apre gia' la finestra Changes, qui non c'e' altro da fare.
        if (command == "commit")
            return;

        var targets = new List<SvnTarget>();
        foreach (var p in paths ?? [])
            if (SvnTarget.Resolve(p) is { } t && !targets.Any(x => string.Equals(x.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
                targets.Add(t);

        if (targets.Count == 0)
        {
            await InfoAsync(Loc.T("Nessun percorso valido selezionato."));
            return;
        }

        if (targets.Any(t => t.WorkingCopyRoot is null))
        {
            await InfoAsync(SvnTarget.NotWorkingCopyMessage);
            return;
        }

        try
        {
            switch (command)
            {
                case "update": await UpdateAsync(targets, ct); break;
                case "updaterev": await UpdateToRevisionAsync(targets, ct); break;
                case "cleanup": await CleanupAsync(targets, ct); break;
                case "lock": await LockAsync(targets, ct); break;
                case "unlock": await UnlockAsync(targets, ct); break;
                case "add": await AddAsync(targets, ct); break;
                case "revert": await RevertAsync(targets, ct); break;
                case "diff": await DiffAsync(targets[0], ct); break;
                case "ignore": await IgnoreAsync(targets, ct); break;
                case "log": await _host.ShowLogAsync(targets[0].Path); break;
                default: throw new ArgumentException(Loc.T("Comando sconosciuto: {0}", command));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // chiusura
        }
        catch (Exception ex)
        {
            BackendLog.Warn($"Comando percorso {command}: {ex.Message}");
            await InfoAsync(Loc.T("Errore ({0}):\n{1}", command, ex.Message));
        }
    }

    private Task InfoAsync(string message) => _host.PromptAsync(message, ["OK"]);

    private async Task<bool> ConfirmAsync(string message) => await _host.PromptAsync(message, ["OK", Loc.T("Annulla")]) == 0;

    private Task UpdateAsync(List<SvnTarget> targets, CancellationToken ct) => UpdateAsync(targets, null, ct);

    private async Task UpdateToRevisionAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        var message = Loc.T("Aggiorna alla revisione indicata (numero intero positivo oppure HEAD).");
        while (true)
        {
            var typed = await _host.PromptTextAsync(message, Loc.T("Revisione"), "HEAD");
            if (typed is null)
                return;
            if (SvnRevision.TryNormalize(typed, out var revision))
            {
                await UpdateAsync(targets, revision, ct);
                return;
            }

            message = Loc.T("\"{0}\" non è una revisione valida: scrivi un numero intero positivo (es. 1234) oppure HEAD.", typed.Trim());
        }
    }

    private async Task CleanupAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        foreach (var t in targets)
            await _svn.CleanupAsync(t.Path, ct);
        await InfoAsync(Loc.T("Cleanup completato."));
        await _refresh();
    }

    private async Task LockAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        if (targets.Any(t => t.IsDirectory))
        {
            await InfoAsync(Loc.T("Il lock si applica ai singoli file: seleziona uno o più file."));
            return;
        }

        var paths = targets.Select(t => t.Path).ToList();
        var message = await _host.PromptTextAsync(Loc.T("Blocca (svn lock):\n{0}\n\nMessaggio facoltativo:", SvnTarget.FormatList(paths)), Loc.T("Messaggio"), string.Empty);
        if (message is null)
            return;

        try
        {
            await _svn.LockAsync(paths, message, force: false, ct);
        }
        catch (InvalidOperationException ex) when (SvnErrors.IsLockedByOther(ex.Message))
        {
            if (!await ConfirmAsync(Loc.T("Almeno un file è già bloccato da un altro utente.\n\n{0}\n\nRubare il lock lo toglie a lui: potrebbe perdere il diritto di committare il file. Procedere?", ex.Message)))
                return;
            await _svn.LockAsync(paths, message, force: true, ct);
        }

        await InfoAsync(Loc.T("Bloccati (lock) {0} file.", paths.Count));
        await _refresh();
    }

    private async Task UnlockAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        if (targets.Any(t => t.IsDirectory))
        {
            await InfoAsync(Loc.T("L'unlock si applica ai singoli file: seleziona uno o più file."));
            return;
        }

        await _svn.UnlockAsync(targets.Select(t => t.Path).ToList(), ct);
        await InfoAsync(Loc.T("Sbloccati (unlock)."));
        await _refresh();
    }

    private async Task UpdateAsync(List<SvnTarget> targets, string? revision, CancellationToken ct)
    {
        var last = string.Empty;
        var conflicts = new List<string>();
        foreach (var t in targets)
        {
            var r = revision is null ? await _svn.UpdateAsync(t.Path, ct) : await _svn.UpdateToRevisionAsync(t.Path, revision, ct);
            last = r.Revision is { } rev ? Loc.T("Aggiornato alla revisione {0}.", rev) : Loc.T("Update completato.");
            conflicts.AddRange(r.ConflictedPaths);
        }

        if (conflicts.Count > 0)
            last += "\n\n" + Loc.T("File in conflitto ({0}):", conflicts.Count) + "\n" + SvnTarget.FormatList(conflicts);
        await InfoAsync(last);
        await _refresh();
    }

    private async Task AddAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        var toAdd = new List<string>();
        foreach (var t in targets)
        {
            if (!t.IsDirectory)
            {
                toAdd.Add(t.Path);
                continue;
            }

            var root = t.WorkingCopyRoot!;
            var prefix = Path.GetRelativePath(root, t.Path).Replace('\\', '/');
            foreach (var s in await _svn.GetStatusAsync(root, ct))
            {
                var rel = s.Path.Replace('\\', '/');
                if (s.Status == SvnStatusKind.Unversioned
                    && (prefix == "." || rel.Equals(prefix, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                    toAdd.Add(Path.Combine(root, s.Path));
            }
        }

        if (toAdd.Count == 0)
        {
            await InfoAsync(Loc.T("Nessun file non versionato da aggiungere."));
            return;
        }

        if (targets.Any(t => t.IsDirectory)
            && !await ConfirmAsync(Loc.T("Aggiungere a SVN {0} elementi non versionati?\n(Attenzione a bin/, obj/, .vs/, packages/)\n\n{1}",
                toAdd.Count, SvnTarget.FormatList(toAdd.Select(p => Path.GetFileName(p)).ToList()))))
            return;

        await _svn.AddAsync(toAdd, ct);
        await InfoAsync(Loc.T("Aggiunti {0} elementi.", toAdd.Count));
        await _refresh();
    }

    private async Task RevertAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        var paths = targets.Select(t => t.Path).ToList();
        if (!await ConfirmAsync(Loc.T("Annullare le modifiche locali su:\n{0}\n\nL'operazione non è reversibile.", SvnTarget.FormatList(paths))))
            return;

        await _svn.RevertAsync(paths, ct);
        await InfoAsync(Loc.T("Modifiche annullate."));
        await _refresh();
    }

    private async Task DiffAsync(SvnTarget t, CancellationToken ct)
    {
        // Cartella: elenco dei file modificati nella finestra SVN Log (clic = diff nativo; "Apri come patch" = vecchio .diff unico).
        if (t.IsDirectory)
            await _host.ShowFolderDiffAsync(t.Path);
        else
            await _diffs.OpenWorkingAsync(t.Path, ct);
    }

    private async Task IgnoreAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        foreach (var t in targets)
            await _svn.AddToIgnoreListAsync(t.Path, ct);
        await InfoAsync(Loc.T("Aggiunto a svn:ignore. La modifica alla proprietà va committata sulla cartella padre."));
        await _refresh();
    }
}
