using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>Un elemento con modifiche pendenti al momento del merge. <c>Status</c> = M | A | D | R; <c>Sha256</c> = contenuto del file (null per cartelle e file eliminati).</summary>
internal sealed record PreMergeEntry(string Path, string Status, bool IsDir, string? Sha256);

/// <summary>
/// Copia di sicurezza delle modifiche pendenti PRIMA di un merge su una working copy sporca: "Annulla merge" fa <c>revert -R</c> e le toglierebbe.
/// Sta in <c>&lt;WC&gt;/.svn-assist/backups/&lt;data-ora&gt;-pre-merge/</c>: <c>changes.patch</c> (<c>svn diff</c> leggibile e riapplicabile: testo, proprieta', eliminazioni),
/// <c>files/</c> (i byte esatti di ogni file modificato o aggiunto: la patch e' testo UTF-8 e perderebbe binari, file vuoti e codifiche diverse) e
/// <c>manifest.json</c>. Il ripristino applica la patch, rimette i byte originali dove differiscono, riaggiunge/rielimina quello che serve e poi
/// <b>verifica</b> (hash dei file, stato di ogni elemento, stesso <c>svn diff</c> di prima): se qualcosa non coincide il backup resta e l'utente lo sa.
/// </summary>
internal sealed class PreMergeSnapshot
{
    private PreMergeSnapshot(string wc, string dir, string diff, IReadOnlyList<PreMergeEntry> entries, IReadOnlyList<string> paths)
    {
        Wc = wc;
        Dir = dir;
        Diff = diff;
        Entries = entries;
        Paths = paths;
    }

    /// <summary>La working copy a cui appartiene: mai ripristinare su un'altra.</summary>
    internal string Wc { get; }

    internal string Dir { get; }
    internal string PatchFile => Path.Combine(Dir, "changes.patch");
    private string FilesDir => Path.Combine(Dir, "files");
    private string ManifestFile => Path.Combine(Dir, "manifest.json");

    /// <summary><c>svn diff</c> di prima del merge (per il confronto finale).</summary>
    internal string Diff { get; }

    internal IReadOnlyList<PreMergeEntry> Entries { get; }

    /// <summary>Percorsi assoluti su cui e' stato fatto il diff.</summary>
    private IReadOnlyList<string> Paths { get; }

    /// <summary>File che il merge non puo' ripristinare bene: stati che bloccano un merge su working copy sporca.</summary>
    internal static bool IsBlocking(SvnStatusKind s) => s is SvnStatusKind.Conflicted or SvnStatusKind.Obstructed or SvnStatusKind.Incomplete or SvnStatusKind.Missing;

    /// <summary>Modifiche pendenti che il backup sa salvare e ripristinare.</summary>
    internal static bool IsPending(SvnStatusKind s) => s is SvnStatusKind.Modified or SvnStatusKind.Added or SvnStatusKind.Deleted or SvnStatusKind.Replaced;

    private static string Kind(SvnStatusKind s) => s switch
    {
        SvnStatusKind.Added => "A",
        SvnStatusKind.Deleted => "D",
        SvnStatusKind.Replaced => "R",
        _ => "M",
    };

    private static string Rel(string wc, string path)
    {
        var p = (Path.IsPathFullyQualified(path) ? Path.GetRelativePath(wc, path) : path).Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.Length == 0 ? "." : p;
    }

    private static string Full(string wc, string rel) => rel == "." ? wc : Path.Combine(wc, rel.Replace('/', Path.DirectorySeparatorChar));

    internal static string Hash(string file)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(file);
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    /// <summary>
    /// Salva le modifiche pendenti. Lancia <see cref="PatchBackup.NoSpaceException"/> se manca lo spazio e <see cref="IOException"/> se la scrittura fallisce:
    /// il merge non parte (con modifiche pendenti non si procede mai senza backup).
    /// </summary>
    internal static async Task<PreMergeSnapshot> CaptureAsync(ISvnService svn, string wc, IReadOnlyList<SvnFileStatus> pending, DateTime? now, CancellationToken ct)
    {
        var entries = new List<PreMergeEntry>();
        long fileBytes = 0;
        foreach (var s in pending)
        {
            var rel = Rel(wc, s.Path);
            var full = Full(wc, rel);
            var isDir = Directory.Exists(full);
            string? sha = null;
            if (!isDir && File.Exists(full))
            {
                sha = Hash(full);
                fileBytes += new FileInfo(full).Length;
            }

            entries.Add(new PreMergeEntry(rel, Kind(s.Status), isDir, sha));
        }

        var paths = entries.Select(e => Full(wc, e.Path)).ToList();
        var diff = await svn.GetPatchAsync(wc, paths, ct);
        var needed = PatchBackup.Needed(Encoding.UTF8.GetByteCount(diff) + fileBytes + 64L * 1024);
        var free = PatchBackup.FreeSpace(wc);
        if (free < needed)
            throw new PatchBackup.NoSpaceException(needed, free);

        PatchBackup.Prune(wc);
        var name = $"{(now ?? DateTime.Now):yyyyMMdd-HHmmss}-pre-merge";
        var dir = Path.Combine(PatchBackup.BackupsFolder(wc), name);
        for (var i = 2; Directory.Exists(dir); i++)
            dir = Path.Combine(PatchBackup.BackupsFolder(wc), $"{name}-{i}");
        var snap = new PreMergeSnapshot(wc, dir, diff, entries, paths);
        try
        {
            Directory.CreateDirectory(snap.FilesDir);
            foreach (var e in entries.Where(e => e.Sha256 is not null))
            {
                var target = Path.Combine(snap.FilesDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Full(wc, e.Path), target, overwrite: true);
            }

            await File.WriteAllBytesAsync(snap.PatchFile, new UTF8Encoding(false).GetBytes(diff), ct);
            await File.WriteAllTextAsync(snap.ManifestFile, JsonSerializer.Serialize(new { version = 1, created = DateTime.Now, wc, entries }, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        catch (Exception) when (Directory.Exists(dir))
        {
            snap.Delete(); // backup a meta': non e' un backup, meglio nessuno che uno ingannevole
            throw;
        }

        return snap;
    }

    /// <summary>
    /// Rilegge dal disco una copia di sicurezza (dopo un riavvio di VS, per "Annulla merge" di un merge interrotto). <c>null</c> se la cartella manca
    /// o e' incompleta: meglio nessuna copia che una copia che ripristina a meta'.
    /// </summary>
    internal static PreMergeSnapshot? Load(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return null;
            var manifest = Path.Combine(dir, "manifest.json");
            var patch = Path.Combine(dir, "changes.patch");
            if (!File.Exists(manifest) || !File.Exists(patch))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var wc = doc.RootElement.GetProperty("wc").GetString() ?? string.Empty;
            var entries = doc.RootElement.GetProperty("entries").EnumerateArray()
                .Select(e => new PreMergeEntry(e.GetProperty("Path").GetString() ?? string.Empty, e.GetProperty("Status").GetString() ?? "M",
                    e.GetProperty("IsDir").GetBoolean(), e.GetProperty("Sha256").ValueKind == JsonValueKind.String ? e.GetProperty("Sha256").GetString() : null))
                .ToList();
            var diff = File.ReadAllText(patch, new UTF8Encoding(false));
            return new PreMergeSnapshot(wc, dir, diff, entries, entries.Select(e => Full(wc, e.Path)).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            BackendLog.Warn("Copia di sicurezza non riletta: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Rimette le modifiche pendenti dopo il <c>revert -R</c> di "Annulla merge". Ritorna i problemi (vuoto = ripristino verificato identico):
    /// con problemi il chiamante tiene il backup e lo dice all'utente.
    /// </summary>
    internal async Task<List<string>> RestoreAsync(ISvnService svn, string wc, CancellationToken ct)
    {
        if (!string.Equals(Path.GetFullPath(wc).TrimEnd('\\', '/'), Path.GetFullPath(Wc).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return [Loc.T("La copia di sicurezza appartiene a un'altra working copy ({0}): non è stato ripristinato nulla.", Wc)];
        var problems = new List<string>();
        // 1) patch: proprieta', eliminazioni, aggiunte con contenuto, testo. Un rifiuto (es. file non UTF-8, che nella patch e' testo alterato) non e' ancora
        //    un problema: i byte originali si rimettono al passo 2 e a decidere e' la verifica finale. La patch fallita entra nel messaggio solo se la verifica fallisce.
        string? patchNote = null;
        try
        {
            var output = await svn.ApplyPatchAsync(wc, PatchFile, dryRun: false, ct);
            var sum = ChangesStore.SummarizePatch(output);
            if (sum.Problems > 0)
            {
                BackendLog.Warn($"Ripristino pre-merge: {sum.Problems} rifiuti o conflitti applicando la patch");
                patchNote = Loc.T("{0} rifiuti o conflitti applicando la patch delle modifiche pendenti.", sum.Problems);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Ripristino pre-merge: patch non applicata: " + ex.Message);
            patchNote = Loc.T("La patch delle modifiche pendenti non si è applicata: {0}", ex.Message.Split('\n')[0]);
        }

        // 2) byte originali dove il file non e' identico (codifiche non UTF-8, binari, file vuoti: la patch non li tiene)
        foreach (var e in Entries.Where(e => e.Sha256 is not null))
        {
            var full = Full(wc, e.Path);
            if (File.Exists(full) && string.Equals(Hash(full), e.Sha256, StringComparison.Ordinal))
                continue;
            var saved = Path.Combine(FilesDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(saved))
            {
                problems.Add(Loc.T("{0}: copia di sicurezza mancante.", e.Path));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(saved, full, overwrite: true);
        }

        // Un hunk rifiutato lascia il file in conflitto e i file di appoggio della patch: con i byte giusti al loro posto si chiude il conflitto e si tolgono.
        foreach (var c in (await svn.GetLocalChangesAsync(wc, ct)).Where(c => c.Status == SvnStatusKind.Conflicted && !c.IsTreeConflict))
        {
            var rel = Rel(wc, c.Path);
            if (Entries.Any(e => !e.IsDir && e.Sha256 is not null && string.Equals(e.Path, rel, StringComparison.OrdinalIgnoreCase)))
                await svn.ResolveAsync(Full(wc, rel), SvnConflictChoice.Working, ct);
        }

        foreach (var e in Entries.Where(e => !e.IsDir))
            foreach (var ext in new[] { ".svnpatch.rej", ".svnpatch.orig" })
                if (File.Exists(Full(wc, e.Path) + ext))
                    File.Delete(Full(wc, e.Path) + ext);

        // 3) aggiunte e eliminazioni che la patch non porta (cartelle, file vuoti, binari): si guarda lo stato com'e' ora
        var status = (await svn.GetStatusAsync(wc, ct)).ToDictionary(s => Rel(wc, s.Path), s => s.Status, StringComparer.OrdinalIgnoreCase);
        // Un file aggiunto che torna non versionato puo' non comparire nello stato (sta sotto una cartella non versionata): conta cio' che e' su disco e non e' gia' aggiunto.
        var toAdd = Entries.Where(e => e.Status == "A" && (File.Exists(Full(wc, e.Path)) || Directory.Exists(Full(wc, e.Path)))
                                       && !(status.TryGetValue(e.Path, out var st) && st != SvnStatusKind.Unversioned))
            .OrderBy(e => e.Path.Count(c => c == '/')).ToList();
        foreach (var dir in toAdd.Where(e => e.IsDir))
            await svn.AddAsync([Full(wc, dir.Path)], ct, nonRecursive: true);
        var files = toAdd.Where(e => !e.IsDir).Select(e => Full(wc, e.Path)).ToList();
        if (files.Count > 0)
            await svn.AddAsync(files, ct);
        var toDelete = Entries.Where(e => e.Status == "D" && (File.Exists(Full(wc, e.Path)) || Directory.Exists(Full(wc, e.Path)))
                                          && !(status.TryGetValue(e.Path, out var st2) && st2 == SvnStatusKind.Deleted))
            .OrderByDescending(e => e.Path.Count(c => c == '/')).Select(e => Full(wc, e.Path)).ToList();
        if (toDelete.Count > 0)
            await svn.DeleteAsync(toDelete, ct, force: true);

        // 4) verifica
        var after = (await svn.GetLocalChangesAsync(wc, ct)).ToDictionary(s => Rel(wc, s.Path), s => Kind(s.Status), StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
        {
            if (!after.TryGetValue(e.Path, out var kind) || kind != e.Status)
                problems.Add(Loc.T("{0}: lo stato non è tornato quello di prima del merge.", e.Path));
            else if (e.Sha256 is not null && (!File.Exists(Full(wc, e.Path)) || Hash(Full(wc, e.Path)) != e.Sha256))
                problems.Add(Loc.T("{0}: il contenuto non è identico a quello di prima del merge.", e.Path));
        }

        if (problems.Count == 0)
        {
            var again = await svn.GetPatchAsync(wc, Paths, ct);
            if (!string.Equals(again.Replace("\r\n", "\n").TrimEnd(), Diff.Replace("\r\n", "\n").TrimEnd(), StringComparison.Ordinal))
                problems.Add(Loc.T("Le modifiche ripristinate non coincidono del tutto con quelle di prima del merge (proprietà o contenuto)."));
        }

        if (problems.Count > 0 && patchNote is not null)
            problems.Insert(0, patchNote);
        return problems;
    }

    /// <summary>Elimina la copia di sicurezza (dopo un ripristino verificato o un commit riuscito). Mai un'eccezione.</summary>
    internal void Delete()
    {
        try { Directory.Delete(Dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: la rimuove Prune */ }
    }
}
