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

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _openRevisionPicker;

    public PathCommands(ISvnService svn, IHost host, Func<Task> refresh, string tempRoot,
        Func<IReadOnlyList<string>, CancellationToken, Task>? openRevisionPicker = null)
    {
        _openRevisionPicker = openRevisionPicker ?? ((_, _) => Task.CompletedTask);
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

        // revertlines: paths = [file, "da-a"] (righe 1-based del file di lavoro); il secondo elemento non e' un percorso.
        if (command == "revertlines")
        {
            await RevertLinesAsync(paths, ct);
            return;
        }

        // log di un file (anche .csproj/.sln: SvnTarget li porterebbe alla cartella): svn log sul solo file
        if (command == "log" && paths is [var logFile] && File.Exists(logFile))
        {
            if (SvnService.FindSvnRoot(Path.GetDirectoryName(logFile)!) is null) await InfoAsync(SvnTarget.NotWorkingCopyMessage);
            else await _host.ShowLogAsync(logFile);
            return;
        }

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

    /// <summary>La scelta della revisione e' la modale della finestra Changes (che l'host ha gia' aperto): qui la si apre sui percorsi selezionati.</summary>
    private Task UpdateToRevisionAsync(List<SvnTarget> targets, CancellationToken ct)
        => _openRevisionPicker(targets.Select(t => t.Path).ToList(), ct);

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

    /// <summary>Righe iniziali (1-based) dei blocchi modificati del file di lavoro; una cancellazione pura punta alla riga che segue il punto.</summary>
    internal async Task<int[]> ChangedBlocksAsync(string file, CancellationToken ct)
    {
        var t = SvnTarget.Resolve(file);
        if (t is null || t.IsDirectory || t.WorkingCopyRoot is null || !File.Exists(t.Path)) return [];
        return LineRevert.ParseHunks(await _svn.GetFileDiffAsync(t.Path, ct))
            .Select(h => h.NewCount > 0 ? h.NewStart : h.NewStart + 1).ToArray();
    }

    /// <summary>Esito di revertlines: niente dialoghi (l'utente vede gia' il risultato nel diff), solo il messaggio di stato in Changes.</summary>
    private Task ToastAsync(string text) => _host.PushAsync("changes",
        System.Text.Json.JsonSerializer.Serialize(new { v = 1, type = "event", name = "toast", text }));

    /// <summary>
    /// "Annulla le modifiche selezionate": riporta a BASE solo le righe del file di lavoro nell'intervallo (vedi <see cref="LineRevert"/>).
    /// Scrive su disco mantenendo encoding, BOM e fine riga; l'host ha gia' salvato il documento aperto.
    /// </summary>
    private async Task RevertLinesAsync(string[] paths, CancellationToken ct)
    {
        try
        {
            var range = paths.Length > 1 ? paths[1].Split('-') : [];
            if (paths.Length < 2 || range.Length != 2 || !int.TryParse(range[0], out var from) || !int.TryParse(range[1], out var to) || from < 1 || to < from)
                throw new ArgumentException(Loc.T("Intervallo di righe non valido."));
            var t = SvnTarget.Resolve(paths[0]);
            if (t is null || t.IsDirectory || !File.Exists(t.Path)) { await ToastAsync(Loc.T("Nessun percorso valido selezionato.")); return; }
            if (t.WorkingCopyRoot is null) { await ToastAsync(SvnTarget.NotWorkingCopyMessage); return; }

            var hunks = LineRevert.ParseHunks(await _svn.GetFileDiffAsync(t.Path, ct));
            if (hunks.Count == 0) { await ToastAsync(Loc.T("Il file non ha modifiche locali da annullare.")); return; }

            var baseFile = Path.Combine(Path.GetTempPath(), "SVNAssist_base_" + Guid.NewGuid().ToString("N") + Path.GetExtension(t.Path));
            string baseText;
            try
            {
                await _svn.ExportBaseAsync(t.Path, baseFile, ct);
                baseText = LineRevert.Decode(await File.ReadAllBytesAsync(baseFile, ct)).Text;
            }
            finally { try { File.Delete(baseFile); } catch { /* best effort */ } }

            var (work, enc, bom) = LineRevert.Decode(await File.ReadAllBytesAsync(t.Path, ct));
            var (text, count) = LineRevert.Apply(work, baseText, hunks, from, to);
            if (count == 0) { await ToastAsync(Loc.T("Nessuna modifica nelle righe selezionate.")); return; }

            // documento aperto: l'host applica il testo al buffer (una sola modifica, Ctrl+Z) e salva; altrimenti si scrive su disco
            if (!await _host.ApplyDocumentTextAsync(t.Path, text))
                await File.WriteAllBytesAsync(t.Path, [.. bom, .. enc.GetBytes(text)], ct);
            await ToastAsync(Loc.T("Annullate {0} righe", count));
            await _refresh();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BackendLog.Warn($"Comando percorso revertlines: {ex.Message}");
            await ToastAsync(Loc.T("Errore ({0}):\n{1}", "revertlines", ex.Message));
        }
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
