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
            var summary = SummarizePatch(await _svnService.ApplyPatchAsync(wc, path, dryRun: false, ct));
            PatchBackup.Delete(path);
            _restoreBackup = null;
            await RefreshAsync(ct, reloadBranch: false, Loc.T("Modifiche ripristinate: {0} file.", summary.Touched) + ProblemsNote(summary));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore ripristino: {0}", ex.Message) + BackupKept(path));
        }
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

    /// <summary>"Create Patch...": <c>svn diff</c> dei file indicati (nessuna chiave = tutte le modifiche) nel file scelto dall'utente.</summary>
    private async Task CreatePatchAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var wc = WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(wc))
        {
            SetStatus(Loc.T("Nessuna working copy configurata."));
            return;
        }

        List<ChangeEntry> entries;
        lock (_stateLock)
            entries = keys.Count > 0 ? ResolveKeys(keys).Entries : _entries.ToList();
        var paths = entries.Where(e => e.Status != SvnStatusKind.Unversioned).Select(e => e.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
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
            var diff = await _svnService.GetPatchAsync(wc, paths, ct);
            if (string.IsNullOrWhiteSpace(diff))
            {
                SetStatus(Loc.T("Nessuna modifica da salvare in una patch."));
                return;
            }

            await File.WriteAllTextAsync(file, diff, new UTF8Encoding(false), ct);
            var binary = PatchBackup.BinaryFiles(diff);
            SetStatus(Loc.T("Patch creata: {0} ({1} file).", file, Regex.Matches(diff, "^Index: ", RegexOptions.Multiline).Count)
                + (binary.Count == 0 ? string.Empty : " " + Loc.T("Attenzione: i file binari non sono nella patch ({0}).", string.Join(", ", binary.Take(5)))));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus(Loc.T("Errore creazione patch: {0}", ex.Message));
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
