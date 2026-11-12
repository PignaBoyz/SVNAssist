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
    private readonly string _tempRoot;

    public PathCommands(ISvnService svn, IHost host, Func<Task> refresh, string tempRoot)
    {
        _svn = svn;
        _host = host;
        _refresh = refresh;
        _tempRoot = tempRoot;
    }

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
            await InfoAsync("Nessun percorso valido selezionato.");
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
                default: throw new ArgumentException($"Comando sconosciuto: {command}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // chiusura
        }
        catch (Exception ex)
        {
            BackendLog.Warn($"Comando percorso {command}: {ex.Message}");
            await InfoAsync($"Errore ({command}):\n{ex.Message}");
        }
    }

    private Task InfoAsync(string message) => _host.PromptAsync(message, ["OK"]);

    private async Task<bool> ConfirmAsync(string message) => await _host.PromptAsync(message, ["OK", "Annulla"]) == 0;

    private Task UpdateAsync(List<SvnTarget> targets, CancellationToken ct) => UpdateAsync(targets, null, ct);

    private async Task UpdateToRevisionAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        var message = "Aggiorna alla revisione indicata (numero intero positivo oppure HEAD).";
        while (true)
        {
            var typed = await _host.PromptTextAsync(message, "Revisione", "HEAD");
            if (typed is null)
                return;
            if (SvnRevision.TryNormalize(typed, out var revision))
            {
                await UpdateAsync(targets, revision, ct);
                return;
            }

            message = $"\"{typed.Trim()}\" non è una revisione valida: scrivi un numero intero positivo (es. 1234) oppure HEAD.";
        }
    }

    private async Task CleanupAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        foreach (var t in targets)
            await _svn.CleanupAsync(t.Path, ct);
        await InfoAsync("Cleanup completato.");
        await _refresh();
    }

    private async Task LockAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        if (targets.Any(t => t.IsDirectory))
        {
            await InfoAsync("Il lock si applica ai singoli file: seleziona uno o più file.");
            return;
        }

        var paths = targets.Select(t => t.Path).ToList();
        var message = await _host.PromptTextAsync("Blocca (svn lock):\n" + SvnTarget.FormatList(paths) + "\n\nMessaggio facoltativo:", "Messaggio", string.Empty);
        if (message is null)
            return;

        try
        {
            await _svn.LockAsync(paths, message, force: false, ct);
        }
        catch (InvalidOperationException ex) when (SvnErrors.IsLockedByOther(ex.Message))
        {
            if (!await ConfirmAsync("Almeno un file è già bloccato da un altro utente.\n\n" + ex.Message
                    + "\n\nRubare il lock lo toglie a lui: potrebbe perdere il diritto di committare il file. Procedere?"))
                return;
            await _svn.LockAsync(paths, message, force: true, ct);
        }

        await InfoAsync($"Bloccati (lock) {paths.Count} file.");
        await _refresh();
    }

    private async Task UnlockAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        if (targets.Any(t => t.IsDirectory))
        {
            await InfoAsync("L'unlock si applica ai singoli file: seleziona uno o più file.");
            return;
        }

        await _svn.UnlockAsync(targets.Select(t => t.Path).ToList(), ct);
        await InfoAsync("Sbloccati (unlock).");
        await _refresh();
    }

    private async Task UpdateAsync(List<SvnTarget> targets, string? revision, CancellationToken ct)
    {
        var last = string.Empty;
        var conflicts = new List<string>();
        foreach (var t in targets)
        {
            var r = revision is null ? await _svn.UpdateAsync(t.Path, ct) : await _svn.UpdateToRevisionAsync(t.Path, revision, ct);
            last = r.Revision is { } rev ? $"Aggiornato alla revisione {rev}." : "Update completato.";
            conflicts.AddRange(r.ConflictedPaths);
        }

        if (conflicts.Count > 0)
            last += $"\n\nFile in conflitto ({conflicts.Count}):\n" + SvnTarget.FormatList(conflicts);
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
            await InfoAsync("Nessun file non versionato da aggiungere.");
            return;
        }

        if (targets.Any(t => t.IsDirectory)
            && !await ConfirmAsync($"Aggiungere a SVN {toAdd.Count} elementi non versionati?\n(Attenzione a bin/, obj/, .vs/, packages/)\n\n"
                + SvnTarget.FormatList(toAdd.Select(p => Path.GetFileName(p)).ToList())))
            return;

        await _svn.AddAsync(toAdd, ct);
        await InfoAsync($"Aggiunti {toAdd.Count} elementi.");
        await _refresh();
    }

    private async Task RevertAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        var paths = targets.Select(t => t.Path).ToList();
        if (!await ConfirmAsync("Annullare le modifiche locali su:\n" + SvnTarget.FormatList(paths) + "\n\nL'operazione non è reversibile."))
            return;

        await _svn.RevertAsync(paths, ct);
        await InfoAsync("Modifiche annullate.");
        await _refresh();
    }

    private async Task DiffAsync(SvnTarget t, CancellationToken ct)
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(t.Path.TrimEnd('\\', '/'));
        if (t.IsDirectory)
        {
            var text = await _svn.GetFileDiffAsync(t.Path, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                await InfoAsync("Nessuna modifica.");
                return;
            }

            var file = Path.Combine(dir, $"Diff {name}.diff");
            await File.WriteAllTextAsync(file, text, ct);
            await _host.OpenFileAsync(file);
            return;
        }

        var left = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "BASE")).FullName, name);
        try
        {
            await _svn.ExportBaseAsync(t.Path, left, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // senza BASE (file non versionato/aggiunto): lato sinistro vuoto
            BackendLog.Info($"BASE non disponibile per {t.Path}: {ex.Message}");
            await File.WriteAllBytesAsync(left, [], ct);
        }

        await _host.OpenDiffAsync(left, t.Path, $"{name} (BASE)", $"{name} (working copy)", $"{name} — BASE ⇄ working copy");
    }

    private async Task IgnoreAsync(List<SvnTarget> targets, CancellationToken ct)
    {
        foreach (var t in targets)
            await _svn.AddToIgnoreListAsync(t.Path, ct);
        await InfoAsync("Aggiunto a svn:ignore. La modifica alla proprietà va committata sulla cartella padre.");
        await _refresh();
    }
}
