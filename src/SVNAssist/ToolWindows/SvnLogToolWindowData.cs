using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Data context (ViewModel) per la tool window SVN Log.
/// Mostra la cronologia delle revisioni e il diff della revisione selezionata.
/// </summary>
/// <remarks>
/// Layout a due pannelli: lista revisioni sopra, diff sotto. Si caricano <see cref="PageSize"/> revisioni
/// alla volta: "Carica altre 50" rilegge il log con un limite più alto (svn log -l N).
/// </remarks>
[DataContract]
internal class SvnLogToolWindowData : NotifyPropertyChangedObject
{
    /// <summary>Revisioni caricate per pagina.</summary>
    internal const int PageSize = 50;

    private readonly ISvnService _svnService;
    private string _workingCopyPath = string.Empty;
    private string _statusMessage = "Inserisci il percorso della working copy e premi Carica log.";
    private string _diffText = string.Empty;
    private int _limit;
    private bool _canLoadMore;

    public SvnLogToolWindowData(ISvnService svnService)
    {
        _svnService = svnService;
        LogEntries = [];
        LoadLogCommand = new AsyncCommand(OnLoadLogAsync);
        LoadMoreCommand = new AsyncCommand(OnLoadMoreAsync);
        SelectRevisionCommand = new AsyncCommand(OnSelectRevisionAsync);
    }

    /// <summary>Lista delle revisioni SVN caricate dal repository.</summary>
    [DataMember]
    public ObservableList<LogEntryItem> LogEntries { get; }

    /// <summary>Percorso della working copy (o URL del repository) da interrogare.</summary>
    [DataMember]
    public string WorkingCopyPath
    {
        get => _workingCopyPath;
        set => SetProperty(ref _workingCopyPath, value);
    }

    /// <summary>Messaggio di stato mostrato in basso.</summary>
    [DataMember]
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Diff della revisione selezionata.</summary>
    [DataMember]
    public string DiffText
    {
        get => _diffText;
        set => SetProperty(ref _diffText, value);
    }

    /// <summary>Probabilmente esistono altre revisioni oltre a quelle caricate.</summary>
    [DataMember]
    public bool CanLoadMore
    {
        get => _canLoadMore;
        set => SetProperty(ref _canLoadMore, value);
    }

    /// <summary>Carica (da capo) le prime revisioni.</summary>
    [DataMember]
    public IAsyncCommand LoadLogCommand { get; }

    /// <summary>Carica altre 50 revisioni.</summary>
    [DataMember]
    public IAsyncCommand LoadMoreCommand { get; }

    /// <summary>Selezione di una revisione: carica il diff (parametro: il numero di revisione).</summary>
    [DataMember]
    public IAsyncCommand SelectRevisionCommand { get; }

    /// <summary>
    /// Imposta la working copy rilevata dalla solution e carica il log se è cambiata
    /// (non azzera un percorso scritto a mano quando la solution non è SVN).
    /// </summary>
    internal async Task ApplyAutoDetectedWorkingCopyAsync(string? svnRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(svnRoot)
            || string.Equals(WorkingCopyPath, svnRoot, StringComparison.OrdinalIgnoreCase))
            return;

        WorkingCopyPath = svnRoot;
        await LoadAsync(PageSize, ct);
    }

    internal Task OnLoadLogAsync(object? parameter, CancellationToken ct) => LoadAsync(PageSize, ct);

    internal Task OnLoadMoreAsync(object? parameter, CancellationToken ct) => LoadAsync(Math.Max(_limit, PageSize) + PageSize, ct);

    /// <summary>Carica le prime <paramref name="limit"/> revisioni (<c>svn log --xml -l</c>).</summary>
    private async Task LoadAsync(int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            StatusMessage = "Inserisci il percorso della working copy.";
            return;
        }

        try
        {
            StatusMessage = "Caricamento log...";
            var entries = await _svnService.GetLogAsync(WorkingCopyPath, limit, ct);

            _limit = limit;
            LogEntries.Clear();
            LogEntries.AddRange(entries.Select(entry => new LogEntryItem
            {
                Revision = entry.Revision,
                Author = entry.Author,
                Date = entry.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                Message = entry.Message,
            }));

            // Se svn ha restituito tutte quelle richieste, ce ne possono essere altre.
            CanLoadMore = entries.Count >= limit;
            StatusMessage = LogEntries.Count == 0
                ? "Nessuna revisione trovata."
                : $"{LogEntries.Count} revisioni caricate.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            StatusMessage = $"Errore: {ex.Message}";
        }
    }

    /// <summary>Carica il diff della revisione scelta (il parametro può arrivare come long, int o string).</summary>
    internal async Task OnSelectRevisionAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not long revision)
        {
            if (parameter is int intRev)
                revision = intRev;
            else if (parameter is string strRev && long.TryParse(strRev, out var parsed))
                revision = parsed;
            else
                return;
        }

        try
        {
            DiffText = "Caricamento diff...";
            StatusMessage = $"Caricamento diff per revisione {revision}...";

            var diff = await _svnService.GetDiffAsync(WorkingCopyPath, revision, ct);

            DiffText = string.IsNullOrWhiteSpace(diff)
                ? "(nessuna differenza testuale per questa revisione)"
                : diff;
            StatusMessage = $"Diff revisione {revision} caricato.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiffText = $"Errore nel caricamento del diff: {ex.Message}";
            StatusMessage = $"Errore diff: {ex.Message}";
        }
    }
}

/// <summary>
/// Rappresenta una revisione nella lista del log (bindabile da XAML via Remote UI).
/// </summary>
[DataContract]
internal class LogEntryItem : NotifyPropertyChangedObject
{
    private long _revision;
    private string _author = string.Empty;
    private string _date = string.Empty;
    private string _message = string.Empty;

    /// <summary>Numero di revisione SVN.</summary>
    [DataMember]
    public long Revision
    {
        get => _revision;
        set => SetProperty(ref _revision, value);
    }

    /// <summary>Autore del commit.</summary>
    [DataMember]
    public string Author
    {
        get => _author;
        set => SetProperty(ref _author, value);
    }

    /// <summary>Data e ora del commit (formattata come stringa).</summary>
    [DataMember]
    public string Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    /// <summary>Messaggio di commit.</summary>
    [DataMember]
    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }
}
