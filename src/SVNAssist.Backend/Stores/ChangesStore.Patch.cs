using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

// Patch (0.5.0): Create Patch, Apply Patch (anteprima con --dry-run) e backup automatico prima dei revert distruttivi.
internal sealed partial class ChangesStore
{
    private const string PatchFilter = "Patch (*.patch;*.diff)|*.patch;*.diff|*.*|*.*";

    /// <summary>Backup (<see cref="PatchBackup"/>) dell'ultimo revert riuscito, offerto con "Ripristina modifiche" nella barra di stato.</summary>
    private string? _restoreBackup;

    /// <summary>Shelve della working copy (<see cref="ShelveFiles"/>), riletti a ogni refresh.</summary>
    private List<ShelveInfo> _shelves = [];

    /// <summary>Un'altra operazione e' partita (o la finestra si chiude): il backup offerto non serve piu' e si elimina.</summary>
    private void DiscardBackup() => PatchBackup.Delete(Interlocked.Exchange(ref _restoreBackup, null));

    /// <summary>
    /// Salva <c>svn diff</c> di <paramref name="paths"/> prima di un revert. Se non c'e' spazio o la scrittura fallisce chiede
    /// "Procedere senza backup?" (No = <c>Proceed</c> false: operazione annullata). <c>Note</c> avvisa dei binari, che non stanno in un diff.
    /// </summary>
    private async Task<(bool Proceed, string? Path, string Note)> BackupBeforeAsync(string operation, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        PatchBackup.Prune(wc);
        long needed = 0;
        try
        {
            var diff = await _svnService.GetPatchAsync(wc, paths, ct);
            if (string.IsNullOrWhiteSpace(diff))
                return (true, null, string.Empty);
            needed = PatchBackup.Needed(Encoding.UTF8.GetByteCount(diff));
            var path = PatchBackup.Write(wc, operation, diff);
            var binary = PatchBackup.BinaryFiles(diff);
            return (true, path, binary.Count == 0 ? string.Empty
                : " " + Loc.T("Attenzione: i file binari non sono nel backup ({0}).", string.Join(", ", binary.Take(5))));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Backup prima del revert: " + ex.Message);
            var message = ex is IOException or UnauthorizedAccessException
                ? Loc.T("Non c'è abbastanza spazio per il backup (servono {0}, liberi {1}). Procedere senza backup?",
                    PatchBackup.FormatBytes(ex is PatchBackup.NoSpaceException ns ? ns.Needed : needed),
                    PatchBackup.FormatBytes(ex is PatchBackup.NoSpaceException n2 ? n2.Free : PatchBackup.FreeSpace(wc)))
                : Loc.T("Backup delle modifiche non riuscito ({0}). Procedere senza backup?", ex.Message);
            return (await _dialogs.AskAsync(message, Loc.T("Sì"), Loc.T("No"), ct), null, string.Empty);
        }
    }

    /// <summary>Revert riuscito: il backup resta e la barra di stato offre "Ripristina modifiche" fino alla prossima operazione.</summary>
    private void OfferRestore(string? backup)
    {
        _restoreBackup = backup;
        NotifyChanged();
    }

    private static string BackupKept(string? backup)
        => backup is null ? string.Empty : "\n" + Loc.T("Le modifiche sono salvate in: {0}", backup);

    private async Task RestoreBackupAsync(string? path, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (path is null || !File.Exists(path) || string.IsNullOrWhiteSpace(wc))
        {
            _restoreBackup = null;
            SetStatus(Loc.T("Errore: backup non trovato."));
            return;
        }

        try
        {
            SetStatus(Loc.T("Ripristino in corso..."));
            var summary = await ApplyPatchReaddAsync(wc, path, ct);
            PatchBackup.Delete(path);
            _restoreBackup = null;
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Modifiche ripristinate: {0} file.", summary.Touched) + ProblemsNote(summary));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore ripristino: {0}", ex.Message) + BackupKept(path));
        }
    }

    /// <summary>
    /// <c>svn patch</c> con riepilogo. svn revert non cancella i file aggiunti (A): restano su disco come '?', e svn patch li salta ("obstructed").
    /// Il contenuto e' gia' quello giusto: basta rimetterli sotto controllo (<see cref="AddedSkipped"/>).
    /// </summary>
    private async Task<(int Touched, int Problems, string Text)> ApplyPatchReaddAsync(string wc, string patchFile, CancellationToken ct)
    {
        var output = await _svnService.ApplyPatchAsync(wc, patchFile, dryRun: false, ct);
        var summary = SummarizePatch(output);
        var readd = AddedSkipped(output, await File.ReadAllTextAsync(patchFile, ct));
        if (readd.Count == 0)
            return summary;
        await _svnService.AddAsync(readd.Select(r => Path.Combine(wc, r)), ct);
        return (summary.Touched + readd.Count, summary.Problems - readd.Count, summary.Text);
    }

    /// <summary>File nuovi nel backup (<c>--- x	(nonexistent)</c> o <c>(revision 0)</c>) che <c>svn patch</c> ha saltato perche' gia' su disco non versionati.</summary>
    internal static List<string> AddedSkipped(string patchOutput, string patch)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(patch, @"^--- (.+?)\t\((?:nonexistent|revision 0)\)", RegexOptions.Multiline))
            added.Add(m.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar));
        return Regex.Matches(patchOutput, @"^Skipped '(.+?)' -- obstructed by unversioned node", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar))
            .Where(added.Contains).ToList();
    }

    private static string ProblemsNote((int Touched, int Problems, string Text) s)
        => s.Problems == 0 ? string.Empty : " " + Loc.T("{0} rifiuti/conflitti da controllare.", s.Problems);

    /// <summary>File toccati (U/A/D/G/M) e problemi (conflitti C, hunk rifiutati, saltati) nell'output di <c>svn patch</c>.</summary>
    internal static (int Touched, int Problems, string Text) SummarizePatch(string output)
    {
        int touched = 0, problems = 0;
        foreach (var line in output.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^C[A-Z ]?\s{2,}\S") || line.Contains("rejected hunk") || line.StartsWith("Skipped", StringComparison.Ordinal))
                problems++;
            else if (Regex.IsMatch(line, @"^[UADGMER][A-Z ]?\s{2,}\S"))
                touched++;
        }

        return (touched, problems, output.Trim());
    }

    /// <summary>Elementi su cui agiscono Create Patch e Shelve: le chiavi indicate; nessuna = tutte le modifiche (.vs escluso).</summary>
    private List<ChangeEntry> PatchEntries(IReadOnlyList<string> keys)
    {
        lock (_stateLock)
            return keys.Count > 0 ? ResolveKeys(keys).Entries : _entries.Where(e => !IsVsFolder(e)).ToList();
    }

    /// <param name="NewFiles">File che il revert non toglie dal disco (non versionati e aggiunti) e che stanno nella patch.</param>
    /// <param name="Excluded">Binari e file vuoti: non stanno in un diff (<c>svn patch</c> non li crea).</param>
    private sealed record PatchBuild(string Diff, int Files, List<string> Excluded, List<string> NewFiles);

    /// <summary>
    /// <c>svn diff</c> dei versionati piu', per ogni file non versionato (le cartelle valgono i file dentro), la sezione "nuovo file" che
    /// <c>svn patch</c> sa creare (<see cref="SvnService.BuildNewFileDiff"/> completo, percorsi relativi alla working copy con '/').
    /// </summary>
    private async Task<PatchBuild> BuildPatchAsync(string wc, IReadOnlyList<ChangeEntry> entries, CancellationToken ct)
    {
        var paths = entries.Where(e => e.Status != SvnStatusKind.Unversioned).Select(e => e.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var text = paths.Count == 0 ? string.Empty : await _svnService.GetPatchAsync(wc, paths, ct);
        var diff = new StringBuilder(text);
        var excluded = PatchBackup.BinaryFiles(text).Select(r => Path.Combine(wc, r)).ToList();
        var files = Regex.Matches(text, "^Index: ", RegexOptions.Multiline).Count - excluded.Count;
        var newFiles = entries.Where(e => e.Status == SvnStatusKind.Added && !Directory.Exists(e.FullPath)).Select(e => e.FullPath).ToList();
        // shortcut: una cartella non versionata include anche i file ignorati (bin, obj) che ci stanno dentro; da rivedere se serve filtrarli
        var loose = entries.Where(e => e.Status == SvnStatusKind.Unversioned)
            .SelectMany(e => Directory.Exists(e.FullPath)
                ? Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(g => g is ".svn" or ".vs" or PatchBackup.FolderName))
                : [e.FullPath])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in loose)
        {
            string section;
            try { section = SvnService.BuildNewFileDiff(f, Path.GetRelativePath(wc, f).Replace('\\', '/'), complete: true); }
            catch (IOException) { section = string.Empty; } // file aperto in esclusiva da un altro programma: va tra gli esclusi, non blocca la patch
            if (section.Length == 0)
            {
                if (File.Exists(f))
                    excluded.Add(f);
                continue;
            }

            if (diff.Length > 0 && diff[^1] != '\n')
                diff.Append('\n');
            diff.Append(section);
            files++;
            newFiles.Add(f);
        }

        return new PatchBuild(diff.ToString(), files, excluded, newFiles);
    }

    private static string ExcludedNote(string wc, IEnumerable<string> excluded)
        => string.Join(", ", excluded.Take(5).Select(f => Path.GetRelativePath(wc, f)));

    /// <summary>"Create Patch...": <c>svn diff</c> dei file indicati (nessuna chiave = tutte le modifiche) nel file scelto dall'utente; i non versionati come file nuovi.</summary>
    private async Task CreatePatchAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        var entries = PatchEntries(keys);
        if (entries.Count == 0)
        {
            SetStatus(Loc.T("Nessuna modifica da salvare in una patch."));
            return;
        }

        var file = await _host.PickFileAsync(true, Loc.T("Create Patch..."), "changes.patch", PatchFilter);
        if (string.IsNullOrWhiteSpace(file))
            return;
        try
        {
            SetStatus(Loc.T("Creazione della patch in corso..."));
            var patch = await BuildPatchAsync(wc, entries, ct);
            if (patch.Files == 0)
            {
                SetStatus(Loc.T("Nessuna modifica da salvare in una patch."));
                return;
            }

            await File.WriteAllTextAsync(file, patch.Diff, new UTF8Encoding(false), ct);
            SetStatus(Loc.T("Patch creata: {0} ({1} file).", file, patch.Files)
                + (patch.Excluded.Count == 0 ? string.Empty : " " + Loc.T("Attenzione: i file binari non sono nella patch ({0}).", ExcludedNote(wc, patch.Excluded))));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore creazione patch: {0}", ex.Message));
        }
    }

    // ── Shelve / Unshelve ────────────────────────────────────────────

    /// <summary>
    /// "Shelve...": la patch dei file indicati in <c>.svn-assist/shelves</c>; se non si "tiene" (<paramref name="keep"/>) i versionati si annullano
    /// (svn revert) e i non versionati e gli aggiunti vanno nel Cestino. Binari e file vuoti non entrano nello shelve e non si toccano.
    /// </summary>
    private async Task ShelveAsync(IReadOnlyList<string> keys, string name, bool keep, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        var entries = string.IsNullOrWhiteSpace(wc) ? [] : PatchEntries(keys);
        if (entries.Count == 0)
        {
            SetStatus(Loc.T("Nessuna modifica da mettere da parte."));
            return;
        }

        name = name.Trim().Length == 0 ? Loc.T("Modifiche del {0}", DateTime.Now.ToString("g")) : name.Trim();
        try
        {
            SetStatus(Loc.T("Shelve in corso..."));
            var patch = await BuildPatchAsync(wc, entries, ct);
            if (patch.Files == 0)
            {
                SetStatus(Loc.T("Nessuna modifica da mettere da parte."));
                return;
            }

            var skipped = ExcludedNote(wc, patch.Excluded);
            if (!keep && patch.Excluded.Count > 0
                && !await _dialogs.ConfirmAsync(Loc.T("Questi file non entrano nello shelve (binari o vuoti) e restano nella working copy con le loro modifiche:\n\n{0}\n\nContinuare?", skipped), ct))
                return;

            ShelveFiles.Write(wc, name, patch.Diff, patch.Files, skipped);
            if (!keep)
            {
                var skip = patch.Excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var revert = RevertPlan(entries.Where(e => e.Status != SvnStatusKind.Unversioned && StatusPresentation.IsRevertable(e.Status) && !skip.Contains(e.FullPath)).ToList());
                if (revert.Count > 0)
                    await RevertTargetsAsync(revert, ct);
                // svn revert lascia su disco i file aggiunti: nel Cestino come i non versionati, poi le cartelle rimaste vuote.
                foreach (var f in patch.NewFiles.Where(File.Exists))
                    DeleteFromDisk(f);
                foreach (var d in entries.Where(e => e.Status is SvnStatusKind.Added or SvnStatusKind.Unversioned && Directory.Exists(e.FullPath)).Select(e => e.FullPath))
                    if (Directory.Exists(d) && !Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
                        DeleteFromDisk(d);
            }

            await RefreshAsync(ct, reloadBranch: false, Loc.T("Shelve creato: {0} ({1} file).", name, patch.Files)
                + (patch.Excluded.Count == 0 ? string.Empty : " " + Loc.T("Attenzione: i file binari non sono nella patch ({0}).", skipped)));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore shelve: {0}", ex.Message));
        }
    }

    /// <summary>"Unshelve" / "Applica e tieni": <c>svn patch</c>; lo shelve si elimina solo se non ci sono rifiuti o conflitti (e non si tiene).</summary>
    private async Task UnshelveAsync(string id, bool keep, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        try
        {
            var file = ShelveFiles.PathOf(wc, id);
            if (!File.Exists(file))
                throw new FileNotFoundException(Loc.T("Shelve non trovato."));
            SetStatus(Loc.T("Applicazione dello shelve in corso..."));
            var done = await ApplyPatchReaddAsync(wc, file, ct);
            var ok = done.Problems == 0;
            if (ok && !keep)
                File.Delete(file);
            await RefreshAsync(ct, reloadBranch: false, ok
                ? (keep ? Loc.T("Shelve applicato: {0} file (resta in Shelved).", done.Touched) : Loc.T("Shelve applicato ed eliminato: {0} file.", done.Touched))
                : Loc.T("Shelve applicato solo in parte: {0} file, {1} rifiuti/conflitti. Lo shelve non è stato eliminato.", done.Touched, done.Problems));
            if (!ok)
                await _host.PromptAsync(Loc.T("La patch non è stata applicata del tutto:\n\n{0}\n\nI conflitti sono in Changes; gli hunk rifiutati sono nei file .svnpatch.rej.", done.Text), ["OK"]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore unshelve: {0}", ex.Message));
        }
    }

    /// <summary>"Salva come .patch...": copia dello shelve dove sceglie l'utente.</summary>
    private async Task SaveShelveAsync(string id, CancellationToken ct)
    {
        try
        {
            var file = ShelveFiles.PathOf(WorkingCopyPath, id);
            var target = await _host.PickFileAsync(true, Loc.T("Salva come .patch..."), id + ".patch", PatchFilter);
            if (string.IsNullOrWhiteSpace(target))
                return;
            File.Copy(file, target, overwrite: true);
            SetStatus(Loc.T("Patch salvata: {0}.", target));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore salvataggio patch: {0}", ex.Message));
        }
    }

    private async Task DeleteShelveAsync(string id, CancellationToken ct)
    {
        try
        {
            var file = ShelveFiles.PathOf(WorkingCopyPath, id);
            var name = ShelveFiles.List(WorkingCopyPath).FirstOrDefault(s => s.Id == id)?.Name ?? id;
            if (!await _dialogs.ConfirmAsync(Loc.T("Eliminare lo shelve \"{0}\"?\n\nIl file .patch viene cancellato e non si recupera.", name), ct))
                return;
            File.Delete(file);
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Shelve eliminato: {0}.", name));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore eliminazione shelve: {0}", ex.Message));
        }
    }

    /// <summary>"Apply Patch...": sceglie il file, anteprima con <c>svn patch --dry-run</c> (file toccati e problemi), poi applica.</summary>
    private async Task ApplyPatchFileAsync(CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        var file = await _host.PickFileAsync(false, Loc.T("Apply Patch..."), "", PatchFilter);
        if (string.IsNullOrWhiteSpace(file))
            return;
        try
        {
            var preview = SummarizePatch(await _svnService.ApplyPatchAsync(wc, file, dryRun: true, ct));
            if (preview.Touched + preview.Problems == 0)
            {
                SetStatus(Loc.T("La patch non cambia nessun file."));
                return;
            }

            if (!await _dialogs.ConfirmAsync(Loc.T("Anteprima della patch ({0} file, {1} rifiuti/conflitti):\n\n{2}\n\nApplicarla alla working copy?",
                    preview.Touched, preview.Problems, string.Join("\n", preview.Text.Split('\n').Take(MaxListedInConfirmation * 4))), ct))
                return;

            SetStatus(Loc.T("Applicazione della patch in corso..."));
            var done = SummarizePatch(await _svnService.ApplyPatchAsync(wc, file, dryRun: false, ct));
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Patch applicata: {0} file toccati.", done.Touched) + ProblemsNote(done));
            if (done.Problems > 0)
                await _host.PromptAsync(Loc.T("La patch non è stata applicata del tutto:\n\n{0}\n\nI conflitti sono in Changes; gli hunk rifiutati sono nei file .svnpatch.rej.", done.Text), ["OK"]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore applicazione patch: {0}", ex.Message));
        }
    }

    /// <summary>Chiavi dal comando (<c>key</c>/<c>keys</c>); nessuna = vuoto (tutte le modifiche).</summary>
    private static IReadOnlyList<string> OptionalKeys(JsonElement args)
        => args.ValueKind == JsonValueKind.Object && (args.TryGetProperty("keys", out _) || args.TryGetProperty("key", out _)) ? Args.Keys(args) : [];
}
