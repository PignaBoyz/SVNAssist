using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Data context (ViewModel) per la tool window SVN Log.
/// Mostra la cronologia delle revisioni e il diff della revisione selezionata.
/// </summary>
/// <remarks>
/// Layout a due pannelli:
/// - Pannello superiore: lista delle revisioni (Revisione, Autore, Data, Messaggio)
/// - Pannello inferiore: diff testuale della revisione selezionata
///
/// Quando l'utente seleziona una revisione nella lista, il comando
/// <see cref="SelectRevisionCommand"/> viene invocato e carica il diff
/// dal repository tramite <see cref="SvnService.GetDiffAsync"/>.
/// </remarks>
[DataContract]
internal class SvnLogToolWindowData : NotifyPropertyChangedObject
{
    private readonly SvnService _svnService;
    private string _workingCopyPath = string.Empty;
    private string _statusMessage = "Inserisci il percorso della working copy e premi Carica Log.";
    private string _diffText = string.Empty;
    private int _maxEntries = 50;

    public SvnLogToolWindowData()
    {
        _svnService = new SvnService();
        LogEntries = [];
        LoadLogCommand = new AsyncCommand(OnLoadLogAsync);
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

    /// <summary>Messaggio di stato mostrato in basso nella tool window.</summary>
    [DataMember]
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Contenuto del diff della revisione selezionata — mostrato nel pannello inferiore.
    /// </summary>
    [DataMember]
    public string DiffText
    {
        get => _diffText;
        set => SetProperty(ref _diffText, value);
    }

    /// <summary>Numero massimo di revisioni da caricare.</summary>
    [DataMember]
    public int MaxEntries
    {
        get => _maxEntries;
        set => SetProperty(ref _maxEntries, value);
    }

    /// <summary>Comando per caricare la cronologia dal repository.</summary>
    [DataMember]
    public IAsyncCommand LoadLogCommand { get; }

    /// <summary>
    /// Comando invocato quando l'utente seleziona una revisione nella lista.
    /// Il numero di revisione viene passato come parametro dal binding XAML.
    /// </summary>
    [DataMember]
    public IAsyncCommand SelectRevisionCommand { get; }

    /// <summary>
    /// Handler del comando Carica Log.
    /// Chiama <c>svn log --xml</c> e popola la lista delle revisioni.
    /// </summary>
    private async Task OnLoadLogAsync(object? parameter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            StatusMessage = "⚠️ Inserisci il percorso della working copy.";
            return;
        }

        try
        {
            StatusMessage = "⏳ Caricamento log...";
            DiffText = string.Empty;

            // Costruisce l'URL file:/// per il repository locale,
            // oppure usa il path/URL così com'è se già valido.
            var targetPath = WorkingCopyPath;

            var entries = await _svnService.GetLogAsync(targetPath, MaxEntries, ct);

            LogEntries.Clear();
            foreach (var entry in entries)
            {
                LogEntries.Add(new LogEntryItem
                {
                    Revision = entry.Revision,
                    Author = entry.Author,
                    Date = entry.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                    Message = entry.Message,
                });
            }

            StatusMessage = LogEntries.Count == 0
                ? "ℹ️ Nessuna revisione trovata."
                : $"📋 {LogEntries.Count} revisioni caricate.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Errore: {ex.Message}";
        }
    }

    /// <summary>
    /// Handler invocato quando l'utente seleziona una revisione.
    /// Carica il diff per quella revisione e lo mostra nel pannello inferiore.
    /// </summary>
    /// <remarks>
    /// Il parametro è il numero di revisione, passato dal binding XAML come
    /// <c>CommandParameter="{Binding Revision}"</c> sul ListBox.
    /// Il tipo arriva come <c>object</c> e va convertito.
    /// </remarks>
    private async Task OnSelectRevisionAsync(object? parameter, CancellationToken ct)
    {
        if (parameter is not long revision)
        {
            // Il parametro potrebbe arrivare come altri tipi numerici dalla serializzazione Remote UI
            if (parameter is int intRev)
                revision = intRev;
            else if (parameter is string strRev && long.TryParse(strRev, out var parsed))
                revision = parsed;
            else
                return;
        }

        try
        {
            DiffText = "⏳ Caricamento diff...";
            StatusMessage = $"⏳ Caricamento diff per revisione {revision}...";

            var diff = await _svnService.GetDiffAsync(WorkingCopyPath, revision, ct);

            DiffText = string.IsNullOrWhiteSpace(diff)
                ? "(nessuna differenza testuale per questa revisione)"
                : diff;

            StatusMessage = $"📄 Diff revisione {revision} caricato.";
        }
        catch (Exception ex)
        {
            DiffText = $"❌ Errore nel caricamento del diff: {ex.Message}";
            StatusMessage = $"❌ Errore diff: {ex.Message}";
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
