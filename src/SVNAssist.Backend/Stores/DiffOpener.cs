using SVNAssist.Rpc;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Apre i diff nell'editor di Visual Studio (diff a due pannelli nativo, <see cref="IHost.OpenDiffAsync"/>) con file
/// temporanei ottenuti da svn (<c>svn export -r</c>): serve solo svn.exe, TortoiseSVN non c'entra.
/// I file stanno in <c>%TEMP%\SVNAssist\diff\&lt;guid&gt;</c> e si cancellano alla chiusura del backend (<see cref="Dispose"/>);
/// quelli rimasti da sessioni finite male li toglie <c>ChangesStore.CleanTemp</c> (piu' vecchi di un giorno).
/// </summary>
internal sealed class DiffOpener : IDisposable
{
    internal static readonly string DefaultTempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist", "diff");

    private readonly ISvnService _svn;
    private readonly IHost _host;
    private readonly string _tempRoot;
    private readonly List<string> _dirs = [];

    public DiffOpener(ISvnService svn, IHost host, string? tempRoot = null)
    {
        _svn = svn;
        _host = host;
        _tempRoot = tempRoot ?? DefaultTempRoot;
    }

    /// <summary>
    /// Revisioni (sinistra, destra) da confrontare per un file toccato dalla revisione <paramref name="rev"/>:
    /// <c>r-1 ↔ r</c>; aggiunto: niente a sinistra; eliminato: niente a destra (<c>null</c> = file vuoto).
    /// </summary>
    internal static (long? Left, long? Right) ForRevision(string action, long rev) => action switch
    {
        "A" => (null, rev),
        "D" => (rev - 1, null),
        _ => (rev - 1, rev),
    };

    /// <summary>Come <see cref="ForRevision"/> per il confronto tra due revisioni qualunque (<c>a ↔ b</c>).</summary>
    internal static (long? Left, long? Right) ForRange(string action, long a, long b) => action switch
    {
        "A" => (null, b),
        "D" => (a, null),
        _ => (a, b),
    };

    /// <summary>Titolo del diff: <c>Program.cs r12 ↔ r13</c>.</summary>
    internal static string Title(string name, long? left, long? right)
        => $"{name} {(left is { } l ? "r" + l : Loc.T("(vuoto)"))} ↔ {(right is { } r ? "r" + r : Loc.T("(vuoto)"))}";

    /// <summary>Diff di un file della working copy: BASE ↔ working copy (BASE vuoto se il file e' nuovo, working copy vuota se e' sparito).</summary>
    public async Task OpenWorkingAsync(string path, CancellationToken ct)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var dir = NewDir();
        var left = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "BASE")).FullName, name);
        try
        {
            await _svn.ExportBaseAsync(path, left, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Info($"BASE non disponibile per {path}: {ex.Message}");
            await File.WriteAllBytesAsync(left, [], ct);
        }

        var right = path;
        if (!File.Exists(right))
        {
            right = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "working")).FullName, name);
            await File.WriteAllBytesAsync(right, [], ct);
        }

        await _host.OpenDiffAsync(left, right, $"{name} (BASE)", $"{name} (working copy)", $"{name} — BASE ⇄ working copy");
    }

    /// <summary>Diff di <paramref name="target"/> (URL o percorso) tra due revisioni; <c>null</c> = lato vuoto.</summary>
    public async Task OpenRevisionsAsync(string target, string name, long? left, long? right, CancellationToken ct)
    {
        var dir = NewDir();
        var l = await ExportSideAsync(target, name, left, Path.Combine(dir, "left"), ct);
        var r = await ExportSideAsync(target, name, right, Path.Combine(dir, "right"), ct);
        var title = Title(name, left, right);
        await _host.OpenDiffAsync(l, r, left is { } a ? $"{name} r{a}" : Loc.T("(vuoto)"), right is { } b ? $"{name} r{b}" : Loc.T("(vuoto)"), title);
    }

    private async Task<string> ExportSideAsync(string target, string name, long? rev, string dir, CancellationToken ct)
    {
        var file = Path.Combine(Directory.CreateDirectory(dir).FullName, name);
        if (rev is { } v)
            await _svn.ExportRevisionAsync(target, v, file, ct);
        else
            await File.WriteAllBytesAsync(file, [], ct);
        return file;
    }

    /// <summary>Scrive un testo (es. una patch) in un file temporaneo e ne restituisce il percorso.</summary>
    public async Task<string> WriteTempAsync(string fileName, string text, CancellationToken ct)
    {
        var bad = Path.GetInvalidFileNameChars();
        var file = Path.Combine(NewDir(), string.Concat(fileName.Select(c => bad.Contains(c) ? '_' : c)));
        await File.WriteAllTextAsync(file, text, ct);
        return file;
    }

    private string NewDir()
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        lock (_dirs)
            _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        lock (_dirs)
        {
            foreach (var d in _dirs)
            {
                try { Directory.Delete(d, recursive: true); }
                catch { /* best effort: file ancora aperto in VS, lo toglie CleanTemp al prossimo avvio */ }
            }

            _dirs.Clear();
        }
    }
}
