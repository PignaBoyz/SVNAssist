using System.IO;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Eventi del file system e cartelle ignorate da svn (0.7.1).
/// Nessuna cartella e' ignorata "a mano" (bin, obj, Debug, Release, packages... non sono piu' scritte nel codice):
/// decide solo svn, cioe' svn:ignore / svn:global-ignores dell'utente e global-ignores della sua configurazione.
/// Le cartelle che svn dice ignorate si imparano con un solo <c>svn status --no-ignore --depth empty</c> per cartella
/// e si ricordano: una raffica di eventi tutta sotto cartelle ignorate (build, restore) non rilancia <c>svn status</c>.
/// </summary>
internal sealed partial class ChangesStore
{
    /// <summary>Oltre questo numero di percorsi in attesa si fa un refresh completo senza classificarli.</summary>
    internal const int MaxPendingFsPaths = 256;

    /// <summary>Oltre questo numero di cartelle da classificare in una raffica si fa un refresh completo.</summary>
    internal const int MaxDirsToClassify = 200;

    /// <summary>Le cartelle classificate si rileggono comunque dopo questo intervallo (svn:ignore cambiato fuori da SVNAssist).</summary>
    internal static readonly TimeSpan IgnoredCacheTtl = TimeSpan.FromMinutes(10);

    private readonly object _fsLock = new();
    private readonly HashSet<string> _fsPending = new(StringComparer.OrdinalIgnoreCase);
    private bool _fsNeedsFull;
    private volatile bool _inAutoRefresh;

    // Cartella -> true se ignorata da svn. Vale per _ignoredCacheWc; si svuota a ogni refresh non automatico.
    private readonly Dictionary<string, bool> _dirIgnored = new(StringComparer.OrdinalIgnoreCase);
    private string _ignoredCacheWc = string.Empty;
    private DateTime _ignoredCacheSince = DateTime.MinValue;

    /// <summary>Evento del watcher su <paramref name="fullPath"/> (anche dai test).</summary>
    internal void OnFsPath(string fullPath)
    {
        var root = _watchedRoot.Length > 0 ? _watchedRoot : WorkingCopyPath;
        if (string.IsNullOrWhiteSpace(root) || WorkingCopyPathFilter.IsNoise(root, fullPath))
            return;

        lock (_fsLock)
        {
            if (IsUnderKnownIgnoredLocked(root, fullPath))
                return;
            if (_fsPending.Count < MaxPendingFsPaths)
                _fsPending.Add(fullPath);
            else
                _fsNeedsFull = true;
        }

        _debouncer.Request();
    }

    /// <summary>Refresh richiesto senza un percorso preciso (overflow del watcher, salvataggio in VS, rientro del focus).</summary>
    private void RequestFullRefresh()
    {
        lock (_fsLock)
            _fsNeedsFull = true;
        _debouncer.Request();
    }

    /// <summary>Svuota le cartelle classificate (ignore cambiato da un'operazione, working copy diversa, refresh manuale).</summary>
    internal void InvalidateIgnoredCache()
    {
        lock (_fsLock)
        {
            _dirIgnored.Clear();
            _ignoredCacheSince = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Prende i percorsi raccolti dal watcher e dice se serve <c>svn status</c>: <c>false</c> se stanno tutti sotto cartelle
    /// ignorate da svn. Senza percorsi (o troppi, o errore di svn) risponde <c>true</c>: meglio un refresh in piu' che uno perso.
    /// </summary>
    internal async Task<bool> NeedsAutoRefreshAsync(string wc, CancellationToken ct)
    {
        List<string> paths;
        bool full;
        lock (_fsLock)
        {
            paths = [.. _fsPending];
            full = _fsNeedsFull || paths.Count == 0;
            _fsPending.Clear();
            _fsNeedsFull = false;
            ResetIgnoredCacheIfStaleLocked(wc);
        }

        if (full)
            return true;

        List<string> candidates;
        lock (_fsLock)
            candidates = paths.Where(p => !IsUnderKnownIgnoredLocked(wc, p)).ToList();
        if (candidates.Count == 0)
            return false;

        // Fuori dal lock: DirsToClassify guarda il disco (cartella o file).
        var chain = candidates.SelectMany(p => DirsToClassify(wc, p)).ToList();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_fsLock)
            foreach (var dir in chain)
                if (!_dirIgnored.ContainsKey(dir))
                    unknown.Add(dir);

        if (unknown.Count == 0)
            return true;
        if (unknown.Count > MaxDirsToClassify)
            return true;

        IReadOnlyCollection<string> ignored;
        try
        {
            ignored = await _svnService.GetIgnoredPathsAsync(wc, [.. unknown], ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Cartelle ignorate non lette: " + ex.Message);
            return true;
        }

        var ignoredSet = new HashSet<string>(ignored.Select(NormalizeDir), StringComparer.OrdinalIgnoreCase);
        lock (_fsLock)
        {
            if (!string.Equals(_ignoredCacheWc, wc, StringComparison.OrdinalIgnoreCase))
                return true; // working copy cambiata nel frattempo
            foreach (var dir in unknown)
                _dirIgnored[dir] = ignoredSet.Contains(dir);
            return candidates.Any(p => !IsUnderKnownIgnoredLocked(wc, p));
        }
    }

    private void ResetIgnoredCacheIfStaleLocked(string wc)
    {
        if (!string.Equals(_ignoredCacheWc, wc, StringComparison.OrdinalIgnoreCase)
            || DateTime.UtcNow - _ignoredCacheSince > IgnoredCacheTtl)
        {
            _dirIgnored.Clear();
            _ignoredCacheWc = wc;
            _ignoredCacheSince = DateTime.UtcNow;
        }
    }

    /// <summary><c>true</c> se <paramref name="path"/> o una delle sue cartelle sotto la radice e' gia' nota come ignorata.</summary>
    private bool IsUnderKnownIgnoredLocked(string wc, string path)
    {
        if (_dirIgnored.Count == 0 || !string.Equals(_ignoredCacheWc, wc, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var dir in AncestorsUnder(wc, path, includeSelf: true))
            if (_dirIgnored.TryGetValue(dir, out var ign) && ign)
                return true;
        return false;
    }

    /// <summary>
    /// Cartelle da chiedere a svn per un evento su <paramref name="path"/>: tutte quelle fra la radice (esclusa) e il file,
    /// piu' il percorso stesso se e' una cartella. svn non dice "ignorata" per una sottocartella di una cartella ignorata
    /// (src\bin\Debug non risulta, src\bin si'), quindi serve tutta la catena; ogni cartella si chiede una volta sola.
    /// </summary>
    private static IEnumerable<string> DirsToClassify(string wc, string path)
        => AncestorsUnder(wc, path, includeSelf: Directory.Exists(path));

    private static IEnumerable<string> AncestorsUnder(string wc, string path, bool includeSelf)
    {
        var root = NormalizeDir(wc);
        var full = NormalizeDir(path);
        if (full.Length <= root.Length || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            yield break;

        var rel = full[(root.Length + 1)..];
        var segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var last = includeSelf ? segments.Length : segments.Length - 1;
        var current = root;
        for (var i = 0; i < last; i++)
        {
            current = current + Path.DirectorySeparatorChar + segments[i];
            yield return current;
        }
    }

    private static string NormalizeDir(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return path.TrimEnd('\\', '/');
    }
}
