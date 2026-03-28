using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.Dialogs;

/// <summary>
/// Data context (ViewModel) per il dialog di commit.
/// Contiene la lista dei file da committare, il messaggio di commit,
/// la selezione della lingua e i comandi per la generazione AI.
/// </summary>
/// <remarks>
/// Questo ViewModel viene usato con <c>ShowDialogAsync</c>:
/// - VS mostra il dialog con bottoni OK/Cancel
/// - L'utente può scrivere il messaggio manualmente o generarlo con AI
/// - "Genera con AI" chiama <see cref="IAiService.GenerateCommitMessageAsync"/>
/// - "Migliora bozza" chiama <see cref="IAiService.ImproveCommitMessageAsync"/>
/// - Il codice chiamante legge <see cref="CommitMessage"/> dal data context
///   e esegue il commit
/// </remarks>
[DataContract]
internal class CommitDialogData : NotifyPropertyChangedObject
{
    private readonly IAiService? _aiService;
    private readonly string _diff;
    private string _commitMessage = string.Empty;
    private string _selectedLanguage = "Auto";
    private string _aiStatusMessage = string.Empty;
    private bool _isAiBusy;

    /// <summary>
    /// Crea una nuova istanza del data context per il CommitDialog.
    /// </summary>
    /// <param name="filePaths">Lista dei file da committare (mostrati nella UI).</param>
    /// <param name="diff">
    /// Diff delle modifiche non committate — viene passato all'AI come contesto.
    /// </param>
    /// <param name="aiService">
    /// Servizio AI per la generazione dei messaggi. Se <c>null</c>, i bottoni
    /// AI mostreranno un messaggio di errore (AI non configurata).
    /// </param>
    /// <param name="defaultLanguage">
    /// Lingua predefinita dalle impostazioni di VS (<c>"auto"</c>, <c>"italiano"</c>, <c>"english"</c>).
    /// Se non specificata, usa <c>"Auto"</c>.
    /// </param>
    public CommitDialogData(
        IReadOnlyList<string> filePaths,
        string diff,
        IAiService? aiService = null,
        string? defaultLanguage = null)
    {
        _diff = diff;
        _aiService = aiService;

        // Mappa la lingua delle impostazioni al valore del ComboBox
        _selectedLanguage = MapLanguageToDisplay(defaultLanguage);

        Files = [];
        foreach (var path in filePaths)
        {
            Files.Add(new CommitFileItem { Path = path });
        }

        Languages = ["Auto", "Italiano", "English"];

        GenerateAiCommand = new AsyncCommand(OnGenerateAiAsync);
        ImproveCommand = new AsyncCommand(OnImproveAsync);
    }

    /// <summary>
    /// Converte il valore della lingua dalle impostazioni al formato del ComboBox UI.
    /// </summary>
    private static string MapLanguageToDisplay(string? language) => language?.ToLowerInvariant() switch
    {
        "italiano" => "Italiano",
        "english" => "English",
        _ => "Auto",
    };

    /// <summary>Lista dei file che verranno committati (read-only nella UI).</summary>
    [DataMember]
    public ObservableList<CommitFileItem> Files { get; }

    /// <summary>Messaggio di commit scritto dall'utente (o generato dall'AI).</summary>
    [DataMember]
    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    /// <summary>Lingue disponibili per la generazione AI del messaggio.</summary>
    [DataMember]
    public ObservableList<string> Languages { get; }

    /// <summary>Lingua selezionata per la generazione AI.</summary>
    [DataMember]
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set => SetProperty(ref _selectedLanguage, value);
    }

    /// <summary>Messaggio di stato dell'AI (mostrato sotto i bottoni).</summary>
    [DataMember]
    public string AiStatusMessage
    {
        get => _aiStatusMessage;
        set => SetProperty(ref _aiStatusMessage, value);
    }

    /// <summary>
    /// Indica se un'operazione AI è in corso — usato per mostrare lo spinner
    /// e disabilitare i bottoni durante la chiamata.
    /// </summary>
    [DataMember]
    public bool IsAiBusy
    {
        get => _isAiBusy;
        set => SetProperty(ref _isAiBusy, value);
    }

    /// <summary>Genera il messaggio di commit con AI analizzando il diff.</summary>
    [DataMember]
    public IAsyncCommand GenerateAiCommand { get; }

    /// <summary>Migliora il messaggio di commit esistente con AI.</summary>
    [DataMember]
    public IAsyncCommand ImproveCommand { get; }

    /// <summary>
    /// Handler per "Genera con AI" — chiama <see cref="IAiService.GenerateCommitMessageAsync"/>
    /// e popola <see cref="CommitMessage"/> con il risultato.
    /// </summary>
    private async Task OnGenerateAiAsync(object? parameter, CancellationToken ct)
    {
        if (_aiService is null)
        {
            AiStatusMessage = "⚠️ AI non configurata — imposta endpoint e API key nelle opzioni.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_diff))
        {
            AiStatusMessage = "⚠️ Nessun diff disponibile — non ci sono modifiche da analizzare.";
            return;
        }

        try
        {
            IsAiBusy = true;
            AiStatusMessage = "🤖 Generazione in corso...";

            var language = SelectedLanguage.ToLowerInvariant();
            CommitMessage = await _aiService.GenerateCommitMessageAsync(_diff, language, ct);

            AiStatusMessage = "✅ Messaggio generato con AI.";
        }
        catch (Exception ex)
        {
            AiStatusMessage = $"❌ Errore AI: {ex.Message}";
        }
        finally
        {
            IsAiBusy = false;
        }
    }

    /// <summary>
    /// Handler per "Migliora bozza" — chiama <see cref="IAiService.ImproveCommitMessageAsync"/>
    /// con il messaggio attuale come bozza.
    /// </summary>
    private async Task OnImproveAsync(object? parameter, CancellationToken ct)
    {
        if (_aiService is null)
        {
            AiStatusMessage = "⚠️ AI non configurata — imposta endpoint e API key nelle opzioni.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            AiStatusMessage = "⚠️ Scrivi prima una bozza del messaggio da migliorare.";
            return;
        }

        try
        {
            IsAiBusy = true;
            AiStatusMessage = "✨ Miglioramento in corso...";

            var language = SelectedLanguage.ToLowerInvariant();
            CommitMessage = await _aiService.ImproveCommitMessageAsync(CommitMessage, _diff, language, ct);

            AiStatusMessage = "✅ Messaggio migliorato con AI.";
        }
        catch (Exception ex)
        {
            AiStatusMessage = $"❌ Errore AI: {ex.Message}";
        }
        finally
        {
            IsAiBusy = false;
        }
    }
}

/// <summary>
/// Rappresenta un file nella lista del commit dialog (sola lettura).
/// </summary>
[DataContract]
internal class CommitFileItem : NotifyPropertyChangedObject
{
    private string _path = string.Empty;

    /// <summary>Percorso del file relativo alla working copy.</summary>
    [DataMember]
    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }
}
