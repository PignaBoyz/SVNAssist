using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Dialogo "SVN Branch/Tag...": stato (aperto, errore, in corso), dati della working copy (ramo, revisione, cartelle dal layout) e controllo del nome.
/// La copia (<c>svn copy</c>) e lo switch successivo li esegue <see cref="ChangesStore"/>.
/// </summary>
internal sealed class BranchTagStore(ISvnService svn, RepoLayoutResolver layouts)
{
    /// <param name="Name">Nome come nel selettore (<c>branches/x</c>).</param>
    /// <param name="Source">Percorso della working copy o URL.</param>
    internal sealed record Plan(string Kind, string Name, string DestUrl, string Source, long? Rev);

    private sealed record Ctx(string From, long Rev, string Root, string BranchesPath, string TagsPath, string BranchesName, string TagsName, string WcUrl);

    private readonly object _lock = new();
    private bool _open, _busy;
    private string _kind = "branch", _error = string.Empty;
    private Ctx? _ctx;

    public event Action? Changed;

    internal bool IsOpen { get { lock (_lock) return _open; } }

    internal BranchTagDto? GetSnapshot(bool dirty)
    {
        lock (_lock)
            return _open && _ctx is { } c ? new BranchTagDto(_kind, c.From, c.Rev, dirty, c.BranchesPath, c.TagsPath, _error, _busy) : null;
    }

    internal async Task OpenAsync(string workingCopy, string kind, CancellationToken ct)
    {
        try
        {
            var info = await svn.GetInfoAsync(workingCopy, ct);
            var layout = await layouts.ResolveAsync(info, ct);
            var pos = layout.Deduce(info.RelativeUrl);
            string Join(string a) => pos.Base.Length == 0 ? a : pos.Base + "/" + a;
            lock (_lock)
            {
                _ctx = new Ctx(pos.Label, info.Revision, info.RepositoryRoot.TrimEnd('/'), Join(layout.Branches), Join(layout.Tags), layout.Branches, layout.Tags, info.Url);
                (_open, _kind, _error, _busy) = (true, kind == "tag" ? "tag" : "branch", string.Empty, false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
                (_open, _ctx) = (false, null);
            BackendLog.Warn("Branch/Tag: svn info fallito: " + ex.Message);
            throw new InvalidOperationException(Loc.T("Impossibile leggere il repository: {0}", ex.Message), ex);
        }

        Changed?.Invoke();
    }

    internal void Close() => Set(open: false);

    internal void SetError(string message) => Set(error: message);

    internal void SetBusy(bool busy) => Set(busy: busy);

    private void Set(bool? open = null, string? error = null, bool? busy = null)
    {
        lock (_lock)
        {
            _open = open ?? _open;
            _error = error ?? (open == false ? string.Empty : _error);
            _busy = busy ?? _busy;
            if (open == false)
                _ctx = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Messaggio di errore per un nome non valido, <c>null</c> se va bene: niente spazi iniziali/finali, caratteri vietati, segmenti vuoti o <c>.</c>/<c>..</c>.</summary>
    internal static string? ValidateName(string name)
    {
        if (name.Length == 0)
            return Loc.T("Inserisci il nome.");
        if (name != name.Trim())
            return Loc.T("Il nome non può iniziare o finire con spazi.");
        if (name.Any(c => char.IsControl(c) || "\\:*?\"<>|".Contains(c)))
            return Loc.T("Il nome contiene caratteri non validi: \\ : * ? \" < > |");
        if (name.Split('/').Any(s => s.Length == 0 || s.Trim() != s || s is "." or ".."))
            return Loc.T("Il nome non è valido: niente / iniziali o finali, segmenti vuoti, \".\" o \"..\".");
        return null;
    }

    /// <summary>
    /// Controlla i dati della richiesta (nome, revisione, nome gia' esistente sul server) e costruisce la copia. Con un errore lo mostra nel dialogo
    /// e ritorna <c>null</c>. <paramref name="source"/> = head | rev | wc.
    /// </summary>
    internal async Task<Plan?> PrepareAsync(string workingCopy, string kind, string name, string source, string revText, CancellationToken ct)
    {
        Ctx? c;
        lock (_lock)
            c = _ctx;
        if (c is null)
            return null;
        string? problem = ValidateName(name);
        long? rev = null;
        if (problem is null && source == "rev")
        {
            if (long.TryParse(revText.Trim().TrimStart('r', 'R'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0)
                rev = r;
            else
                problem = Loc.T("Numero di revisione non valido.");
        }

        var folder = kind == "tag" ? c.TagsPath : c.BranchesPath;
        var path = folder + "/" + name;
        if (problem is null)
        {
            try
            {
                var parts = path.Split('/');
                var parent = SwitchPickerStore.UrlOf(c.Root, string.Join("/", parts[..^1]));
                if ((await svn.ListEntriesAsync(parent, ct)).Any(e => e.Name == parts[^1]))
                    problem = Loc.T("{0} esiste già nel repository: scegli un altro nome.", (kind == "tag" ? c.TagsName : c.BranchesName) + "/" + name);
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                problem = Loc.T("Impossibile controllare il repository: {0}", ex.Message);
            }
        }

        if (problem is not null)
        {
            SetError(problem);
            return null;
        }

        return new Plan(kind, (kind == "tag" ? c.TagsName : c.BranchesName) + "/" + name, SwitchPickerStore.UrlOf(c.Root, path), source == "wc" ? workingCopy : c.WcUrl, rev);
    }
}
