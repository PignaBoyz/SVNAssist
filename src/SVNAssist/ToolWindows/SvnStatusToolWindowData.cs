using System.Net.Http;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.RpcContracts.Notifications;
using SVNAssist.Dialogs;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Data context (ViewModel) per la tool window SVN Status.
/// Contiene la lista dei file modificati, i comandi Refresh e Commit,
/// e la logica per interagire con <see cref="SvnService"/>.
/// </summary>
/// <remarks>
/// Nel modello VisualStudio.Extensibility, il data context deve:
/// - Avere l'attributo <c>[DataContract]</c> — i dati vengono serializzati
///   e inviati al processo di VS tramite un protocollo remoto
/// - Le proprietà bindabili devono avere <c>[DataMember]</c>
/// - Ereditare da <see cref="NotifyPropertyChangedObject"/> per il change notification
/// - Usare <see cref="IAsyncCommand"/> per i comandi (non <c>ICommand</c>)
///
/// Questo è il pattern "Remote UI": il XAML gira nel processo VS,
/// i dati e la logica girano nel processo dell'estensione (out-of-process).
/// </remarks>
[DataContract]
internal class SvnStatusToolWindowData : NotifyPropertyChangedObject
{
    private readonly SvnService _svnService;
    private readonly VisualStudioExtensibility _extensibility;
    private readonly SettingsService _settingsService;

    /// <summary>
    /// HttpClient singleton per le chiamate AI — non va ricreato ad ogni chiamata
    /// per evitare socket exhaustion.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new();

    private string _workingCopyPath = string.Empty;
    private string _statusMessage = "Inserisci il percorso della working copy e premi Refresh.";

    public SvnStatusToolWindowData(VisualStudioExtensibility extensibility, SettingsService settingsService)
    {
        _extensibility = extensibility;
        _settingsService = settingsService;
        _svnService = new SvnService();
        Files = [];
        RefreshCommand = new AsyncCommand(OnRefreshAsync);
        CommitSelectedCommand = new AsyncCommand(OnCommitSelectedAsync);
    }

    /// <summary>Lista dei file con stato modificato nella working copy.</summary>
    [DataMember]
    public ObservableList<FileStatusItem> Files { get; }

    /// <summary>Percorso della working copy SVN da analizzare.</summary>
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

    /// <summary>Comando Refresh — ricarica la lista dei file modificati.</summary>
    [DataMember]
    public IAsyncCommand RefreshCommand { get; }

    /// <summary>Comando Commit — apre il dialog per committare i file selezionati.</summary>
    [DataMember]
    public IAsyncCommand CommitSelectedCommand { get; }

    /// <summary>
    /// Handler del comando Refresh.
    /// Chiama <c>svn status --xml</c> e popola la lista dei file.
    /// </summary>
    private async Task OnRefreshAsync(object? parameter, CancellationToken ct)
    {
        // Se il campo è vuoto, prova a leggere il percorso dalle impostazioni di VS
        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            var settingsPath = await _settingsService.GetWorkingCopyPathAsync(ct);
            if (!string.IsNullOrWhiteSpace(settingsPath))
                WorkingCopyPath = settingsPath;
        }

        if (string.IsNullOrWhiteSpace(WorkingCopyPath))
        {
            StatusMessage = "⚠️ Inserisci il percorso della working copy (o configuralo in Tools > Options > SVNAssist).";
            return;
        }

        try
        {
            StatusMessage = "⏳ Caricamento in corso...";
            var statuses = await _svnService.GetStatusAsync(WorkingCopyPath, ct);

            Files.Clear();
            foreach (var status in statuses)
            {
                Files.Add(new FileStatusItem
                {
                    Path = status.Path,
                    Status = status.Status.ToString(),
                    IsSelected = false,
                });
            }

            StatusMessage = Files.Count == 0
                ? "✅ Nessuna modifica rilevata."
                : $"📋 {Files.Count} file modificati.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Errore: {ex.Message}";
        }
    }

    /// <summary>
    /// Handler del comando Commit selezionati.
    /// Mostra il CommitDialog modale — se l'utente clicca OK, esegue il commit SVN.
    /// </summary>
    /// <remarks>
    /// <see cref="ShellExtensibility.ShowDialogAsync"/> mostra un dialog modale
    /// con un <see cref="IRemoteUserControl"/> come contenuto.
    /// VS aggiunge automaticamente i bottoni OK/Cancel.
    /// Il risultato indica quale bottone è stato premuto.
    ///
    /// Il diff della working copy viene ottenuto prima di aprire il dialog
    /// e passato a <see cref="CommitDialogData"/> come contesto per l'AI.
    /// L'<see cref="AiService"/> viene creato con endpoint/model hardcoded per ora —
    /// lo Step 9 (SettingsService) li renderà configurabili.
    /// </remarks>
    private async Task OnCommitSelectedAsync(object? parameter, CancellationToken ct)
    {
        var selectedFiles = Files.Where(f => f.IsSelected).ToList();

        if (selectedFiles.Count == 0)
        {
            StatusMessage = "⚠️ Seleziona almeno un file per il commit.";
            return;
        }

        var filePaths = selectedFiles.Select(f => f.Path).ToList();

        try
        {
            // Ottieni il diff delle modifiche non committate per il contesto AI
            StatusMessage = "⏳ Recupero diff...";
            var diff = await _svnService.GetWorkingCopyDiffAsync(WorkingCopyPath, ct);

            // Crea AiService leggendo endpoint, API key e modello dalle impostazioni.
            // Se la API key è vuota, i comandi AI mostreranno "AI non configurata".
            IAiService? aiService = await CreateAiServiceAsync(ct);

            // Legge la lingua predefinita dalle impostazioni
            var defaultLanguage = await _settingsService.GetDefaultLanguageAsync(ct);

            var dialogData = new CommitDialogData(filePaths, diff, aiService, defaultLanguage);

            using var dialogControl = new CommitDialogControl(dialogData);

            // Mostra il dialog modale — VS renderizza il XAML e gestisce OK/Cancel
            DialogResult result = await _extensibility.Shell().ShowDialogAsync(
                dialogControl,
                "SVN Commit",
                DialogOption.OKCancel,
                ct);

            if (result != DialogResult.OK)
            {
                StatusMessage = "❌ Commit annullato dall'utente.";
                return;
            }

            // L'utente ha cliccato OK — esegui il commit con il messaggio inserito
            var message = dialogData.CommitMessage?.Trim();

            if (string.IsNullOrEmpty(message))
            {
                StatusMessage = "⚠️ Il messaggio di commit non può essere vuoto.";
                return;
            }

            StatusMessage = "⏳ Commit in corso...";

            // Costruisco i path completi per SvnService
            var fullPaths = filePaths.Select(p =>
                System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(WorkingCopyPath, p));

            await _svnService.CommitAsync(fullPaths, message, ct);

            StatusMessage = $"✅ Commit completato: {filePaths.Count} file committati.";

            // Aggiorna la lista dei file dopo il commit
            await OnRefreshAsync(null, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Errore durante il commit: {ex.Message}";
        }
    }

    /// <summary>
    /// Crea un'istanza di <see cref="AiService"/> leggendo le impostazioni da VS.
    /// Restituisce <c>null</c> se l'AI non è configurata (API key vuota).
    /// </summary>
    /// <remarks>
    /// Endpoint, API key e modello vengono letti dalle impostazioni di VS
    /// tramite <see cref="SettingsService"/> (configurabili in Tools &gt; Options &gt; SVNAssist).
    /// L'API key vuota fa sì che i bottoni AI mostrino "AI non configurata"
    /// finché l'utente non la imposta.
    /// </remarks>
    private async Task<IAiService?> CreateAiServiceAsync(CancellationToken ct)
    {
        var endpoint = await _settingsService.GetAiEndpointAsync(ct);
        var apiKey = await _settingsService.GetAiApiKeyAsync(ct);
        var model = await _settingsService.GetAiModelAsync(ct);

        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        SharedHttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        return new AiService(SharedHttpClient, endpoint, model);
    }
}

/// <summary>
/// Rappresenta un file nella lista della tool window Status.
/// Ogni proprietà è bindabile dal XAML tramite Remote UI.
/// </summary>
[DataContract]
internal class FileStatusItem : NotifyPropertyChangedObject
{
    private string _path = string.Empty;
    private string _status = string.Empty;
    private bool _isSelected;

    /// <summary>Percorso del file relativo alla working copy.</summary>
    [DataMember]
    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    /// <summary>Stato SVN del file (Modified, Added, Deleted, ecc.).</summary>
    [DataMember]
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Se il file è selezionato per il commit.</summary>
    [DataMember]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
