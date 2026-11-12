using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Selettore di "SVN Switch...": elenco di trunk, branch e tag del repository (<c>svn list</c> sulle cartelle del layout,
/// dedotte dall'URL della working copy). Gli elenchi stanno in cache per la sessione; "Aggiorna" li rilegge.
/// Tiene solo l'elenco; lo switch lo esegue <see cref="ChangesStore"/>.
/// </summary>
internal sealed class SwitchPickerStore
{
    internal sealed record Target(string Name, string Kind, string Url, bool Current);

    private readonly ISvnService _svn;
    private readonly ISvnAssistSettings _settings;
    private readonly object _lock = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _open, _loading;
    private string _error = string.Empty;
    private string _workingCopy = string.Empty;
    private IReadOnlyList<Target> _items = [];

    public SwitchPickerStore(ISvnService svn, ISvnAssistSettings settings)
    {
        _svn = svn;
        _settings = settings;
    }

    public event Action? Changed;

    internal bool IsOpen { get { lock (_lock) return _open; } }

    internal SwitchPickerDto GetSnapshot()
    {
        lock (_lock)
            return new SwitchPickerDto(_open, _loading, _error, _items.Select(i => new SwitchItemDto(i.Name, i.Kind, i.Current)).ToList());
    }

    /// <summary>Elemento dell'elenco con quel nome (<c>branches/x</c>), <c>null</c> se non c'e': e' l'unica via per ottenere l'URL di uno switch.</summary>
    internal Target? Find(string name)
    {
        lock (_lock)
            return _items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Apre il selettore (o lo ricarica con <paramref name="refresh"/>, scartando la cache).</summary>
    internal async Task OpenAsync(string workingCopy, CancellationToken ct, bool refresh = false)
    {
        lock (_lock)
        {
            _open = true;
            _loading = true;
            _error = string.Empty;
            _workingCopy = workingCopy;
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
            if ((await top).Contains(layout.Trunk))
                items.Add(new Target(layout.Trunk, "trunk", UrlOf(root, Join(layout.Trunk)), pos.Kind == "trunk"));
            foreach (var b in await branches)
                items.Add(new Target(layout.Branches + "/" + b, "branch", UrlOf(root, Join(layout.Branches + "/" + b)), pos.Kind == "branch" && pos.Name == b));
            foreach (var t in await tags)
                items.Add(new Target(layout.Tags + "/" + t, "tag", UrlOf(root, Join(layout.Tags + "/" + t)), pos.Kind == "tag" && pos.Name == t));
            lock (_lock)
                _items = items;
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

    internal Task RefreshAsync(CancellationToken ct) => IsOpen ? OpenAsync(_workingCopy, ct, refresh: true) : Task.CompletedTask;

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

    private async Task<IReadOnlyList<string>> CachedAsync(string url, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(url, out var hit))
                return hit;
        }

        var list = await _svn.ListFoldersAsync(url, ct);
        lock (_lock)
            _cache[url] = list;
        return list;
    }
}
