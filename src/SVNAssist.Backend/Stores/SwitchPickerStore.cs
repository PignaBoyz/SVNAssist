using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Selettore di "SVN Switch...": elenco di trunk, branch e tag del repository (<c>svn list</c> sulle cartelle del layout,
/// dedotte dall'URL della working copy). Gli elenchi stanno in cache per la sessione; "Aggiorna" li rilegge.
/// Tiene solo l'elenco; lo switch lo esegue <see cref="ChangesStore"/>. Lo stesso selettore serve a "SVN Merge...": in modo <c>merge</c> il ramo corrente non e' tra le voci.
/// </summary>
internal sealed class SwitchPickerStore
{
    /// <param name="Path">Percorso dalla radice del repository (<c>progetto/branches/x</c>, senza <c>^/</c>).</param>
    /// <param name="Incomplete">Destinazione di uno switch interrotto a meta': non e' "corrente" (la working copy e' mista), sceglierla completa lo switch.</param>
    internal sealed record Target(string Name, string Kind, string Url, bool Current, string Path, long Rev = 0, string Author = "", DateTimeOffset? Date = null, bool Incomplete = false);

    /// <summary>Quanti nomi si ricordano per repository ("usati di recente").</summary>
    internal const int MaxRecent = 8;

    private readonly ISvnService _svn;
    private readonly ISvnAssistSettings _settings;
    private readonly string _recentFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, IReadOnlyList<SvnListEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _open, _loading;
    private string _error = string.Empty;
    private string _workingCopy = string.Empty, _repoRoot = string.Empty;
    private string _mode = "switch";
    private IReadOnlyList<Target> _items = [];
    private IReadOnlyList<string> _recent = [];

    /// <param name="recentFile">File JSON degli ultimi usati per repository (default <c>%LOCALAPPDATA%\SVNAssist\recent-targets.json</c>).</param>
    public SwitchPickerStore(ISvnService svn, ISvnAssistSettings settings, string? recentFile = null)
    {
        _svn = svn;
        _settings = settings;
        _recentFile = recentFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "recent-targets.json");
    }

    public event Action? Changed;

    /// <summary>URL della destinazione di uno switch non completato sulla working copy (o <c>null</c>): quella voce non e' "corrente".</summary>
    internal Func<string, string?>? PendingUrl { get; set; }

    internal bool IsOpen { get { lock (_lock) return _open; } }

    internal SwitchPickerDto GetSnapshot()
    {
        lock (_lock)
            return new SwitchPickerDto(_open, _mode, _loading, _error,
                _items.Select(i => new SwitchItemDto(i.Name, i.Kind, i.Current, i.Rev, i.Author, i.Date?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, i.Incomplete ? true : null)).ToList(),
                _recent.Where(r => _items.Any(i => i.Name == r)).ToList());
    }

    /// <summary>Ricorda <paramref name="name"/> come ultimo usato per il repository corrente (in cima, senza doppioni, al massimo <see cref="MaxRecent"/>). Best effort.</summary>
    internal void RememberUsed(string name)
    {
        string root;
        lock (_lock)
            root = _repoRoot;
        if (root.Length == 0)
            return;
        try
        {
            var all = ReadRecent();
            var list = all.TryGetValue(root, out var cur) ? cur : [];
            all[root] = list.Where(n => n != name).Prepend(name).Take(MaxRecent).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_recentFile)!);
            File.WriteAllText(_recentFile, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackendLog.Warn("Ultimi usati non salvati: " + ex.Message);
        }
    }

    private Dictionary<string, List<string>> ReadRecent()
    {
        try
        {
            return File.Exists(_recentFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_recentFile)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return []; // file assente o rovinato: si riparte da zero
        }
    }

    /// <summary>Elemento dell'elenco con quel nome (<c>branches/x</c>), <c>null</c> se non c'e': e' l'unica via per ottenere l'URL di uno switch.</summary>
    internal Target? Find(string name)
    {
        lock (_lock)
            return _items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Apre il selettore (o lo ricarica con <paramref name="refresh"/>, scartando la cache).</summary>
    internal async Task OpenAsync(string workingCopy, CancellationToken ct, bool refresh = false, string mode = "switch")
    {
        lock (_lock)
        {
            _open = true;
            _loading = true;
            _error = string.Empty;
            _workingCopy = workingCopy;
            _mode = mode;
            if (!refresh)
                _items = [];
            if (refresh)
                _cache.Clear();
        }

        Changed?.Invoke();
        try
        {
            var info = await _svn.GetInfoAsync(workingCopy, ct);
            var layout = RepoLayout.Parse(await _settings.GetRepoLayoutAsync(ct));
            var pos = layout.Deduce(info.RelativeUrl);
            var root = info.RepositoryRoot.TrimEnd('/');
            string Join(string a) => pos.Base.Length == 0 ? a : pos.Base + "/" + a;

            var top = CachedAsync(UrlOf(root, pos.Base), ct);
            var branches = CachedAsync(UrlOf(root, Join(layout.Branches)), ct);
            var tags = CachedAsync(UrlOf(root, Join(layout.Tags)), ct);
            await Task.WhenAll(top, branches, tags);

            var items = new List<Target>();
            if ((await top).FirstOrDefault(e => e.Name == layout.Trunk) is { } trunk)
                items.Add(new Target(layout.Trunk, "trunk", UrlOf(root, Join(layout.Trunk)), pos.Kind == "trunk", Join(layout.Trunk), trunk.Revision, trunk.Author, trunk.Date));
            foreach (var b in await branches)
                items.Add(new Target(layout.Branches + "/" + b.Name, "branch", UrlOf(root, Join(layout.Branches + "/" + b.Name)), pos.Kind == "branch" && pos.Name == b.Name, Join(layout.Branches + "/" + b.Name), b.Revision, b.Author, b.Date));
            foreach (var t in await tags)
                items.Add(new Target(layout.Tags + "/" + t.Name, "tag", UrlOf(root, Join(layout.Tags + "/" + t.Name)), pos.Kind == "tag" && pos.Name == t.Name, Join(layout.Tags + "/" + t.Name), t.Revision, t.Author, t.Date));
            // Switch interrotto: la radice dice gia' "ramo nuovo" ma parte della working copy e' sul vecchio. Quella voce non e' la corrente.
            if (PendingUrl?.Invoke(workingCopy) is { } pending)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i].Url, pending, StringComparison.Ordinal))
                        items[i] = items[i] with { Current = false, Incomplete = true };
                }
            }

            var recent = ReadRecent().GetValueOrDefault(root) ?? [];
            lock (_lock)
            {
                _items = mode == "merge" ? items.Where(i => !i.Current).ToList() : items; // non si unisce un ramo in se stesso
                _repoRoot = root;
                _recent = recent;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
                _error = Loc.T("Impossibile leggere i rami del repository: {0}", ex.Message);
        }
        finally
        {
            lock (_lock)
                _loading = false;
            Changed?.Invoke();
        }
    }

    internal Task RefreshAsync(CancellationToken ct) => IsOpen ? OpenAsync(_workingCopy, ct, refresh: true, mode: _mode) : Task.CompletedTask;

    internal string Mode { get { lock (_lock) return _mode; } }

    internal void Close()
    {
        lock (_lock)
        {
            _open = false;
            _items = [];
            _error = string.Empty;
        }

        Changed?.Invoke();
    }

    internal void SetError(string message)
    {
        lock (_lock)
            _error = message;
        Changed?.Invoke();
    }

    /// <summary>URL = radice + percorso (ogni segmento codificato: nomi con spazi o accenti).</summary>
    internal static string UrlOf(string root, string path)
        => path.Length == 0 ? root : root + "/" + string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private async Task<IReadOnlyList<SvnListEntry>> CachedAsync(string url, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(url, out var hit))
                return hit;
        }

        var list = await _svn.ListEntriesAsync(url, ct);
        lock (_lock)
            _cache[url] = list;
        return list;
    }
}
