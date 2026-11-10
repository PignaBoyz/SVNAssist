using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Services;

namespace SVNAssist.ToolWindows;

/// <summary>
/// Messaggio di commit con le azioni AI (Genera / Migliora) e la scelta della lingua nel pannello.
/// </summary>
/// <remarks>
/// Non conosce la lista file: riceve da chi lo crea le funzioni che restituiscono i percorsi
/// selezionati e la working copy, così resta testabile da solo.
/// </remarks>
[DataContract]
internal sealed class CommitMessageViewModel : NotifyPropertyChangedObject
{
    private const string Italian = "Italiano";
    private const string English = "English";
    private const string Auto = "Auto";

    private readonly ISvnService _svnService;
    private readonly IAiServiceFactory _aiServiceFactory;
    private readonly IAiSetupPrompt _aiSetupPrompt;
    private readonly ISvnAssistSettings _settings;
    private readonly Func<IReadOnlyList<string>> _selectedPaths;
    private readonly Func<string> _workingCopyPath;

    private string _text = string.Empty;
    private string _aiStatus = string.Empty;
    private bool _isBusy;
    private string _selectedLanguage = Auto;
    private bool _languageChosenByUser;
    private bool _languageLoaded;

    public CommitMessageViewModel(
        ISvnAssistSettings settings,
        ISvnService svnService,
        IAiServiceFactory aiServiceFactory,
        IAiSetupPrompt aiSetupPrompt,
        Func<IReadOnlyList<string>> selectedPaths,
        Func<string> workingCopyPath)
    {
        _settings = settings;
        _svnService = svnService;
        _aiServiceFactory = aiServiceFactory;
        _aiSetupPrompt = aiSetupPrompt;
        _selectedPaths = selectedPaths;
        _workingCopyPath = workingCopyPath;
        LanguageOptions = [Italian, English, Auto];
        GenerateCommand = new AsyncCommand(GenerateAsync);
        ImproveCommand = new AsyncCommand(ImproveAsync);
    }

    /// <summary>Testo del messaggio (scritto dall'utente o generato dall'AI).</summary>
    [DataMember]
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>Stato dell'AI (es. "Generazione in corso...", "Messaggio generato con Claude Code.").</summary>
    [DataMember]
    public string AiStatus
    {
        get => _aiStatus;
        set => SetProperty(ref _aiStatus, value);
    }

    /// <summary>Un'operazione AI è in corso.</summary>
    [DataMember]
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            RaiseNotifyPropertyChangedEvent(nameof(IsIdle));
        }
    }

    /// <summary>Nessuna operazione AI in corso (abilita i pulsanti).</summary>
    [DataMember]
    public bool IsIdle => !_isBusy;

    /// <summary>Lingue selezionabili nel pannello.</summary>
    [DataMember]
    public ObservableList<string> LanguageOptions { get; }

    /// <summary>
    /// Lingua scelta nel pannello ("Italiano" / "English" / "Auto"). Vale per la sessione:
    /// non viene riscritta nelle impostazioni.
    /// </summary>
    [DataMember]
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.IsNullOrEmpty(value))
                return;

            _languageChosenByUser = true;
            SetProperty(ref _selectedLanguage, value);
        }
    }

    /// <summary>Genera il messaggio dal diff dei file selezionati.</summary>
    [DataMember]
    public IAsyncCommand GenerateCommand { get; }

    /// <summary>Migliora la bozza scritta dall'utente.</summary>
    [DataMember]
    public IAsyncCommand ImproveCommand { get; }

    /// <summary>Legge la lingua iniziale dalle impostazioni (se l'utente non ha già scelto).</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        if (_languageLoaded)
            return;

        _languageLoaded = true;
        var stored = (await _settings.GetDefaultLanguageAsync(ct)).Trim().ToLowerInvariant();
        if (_languageChosenByUser)
            return;

        SetProperty(ref _selectedLanguage, stored switch
        {
            "italiano" => Italian,
            "english" => English,
            _ => Auto,
        }, nameof(SelectedLanguage));
    }

    /// <summary>Valore da passare a <see cref="IAiService"/>: "italiano", "english" o "auto".</summary>
    internal string LanguageForAi => _selectedLanguage switch
    {
        Italian => "italiano",
        English => "english",
        _ => "auto",
    };

    internal Task GenerateAsync(object? parameter, CancellationToken ct)
        => RunAiAsync(improve: false, ct);

    internal Task ImproveAsync(object? parameter, CancellationToken ct)
        => RunAiAsync(improve: true, ct);

    /// <summary>Flusso comune a Genera e Migliora: provider, file selezionati, diff minimo, chiamata AI.</summary>
    private async Task RunAiAsync(bool improve, CancellationToken ct)
    {
        var ai = await _aiServiceFactory.CreateAsync(ct);
        if (ai is null)
        {
            AiStatus = "Nessun provider AI disponibile (Copilot, Claude o token GitHub).";
            await _aiSetupPrompt.ShowMissingTokenHelpAsync(ct);
            return;
        }

        if (improve && string.IsNullOrWhiteSpace(Text))
        {
            AiStatus = "Scrivi prima una bozza del messaggio da migliorare.";
            return;
        }

        var workingCopy = _workingCopyPath();
        if (string.IsNullOrWhiteSpace(workingCopy))
        {
            AiStatus = "Nessuna working copy configurata.";
            return;
        }

        // L'AI descrive solo i file selezionati (gli stessi che finiranno nel commit).
        var paths = _selectedPaths();
        if (paths.Count == 0)
        {
            AiStatus = improve
                ? "Seleziona almeno un file per migliorare il messaggio."
                : "Seleziona almeno un file per generare il messaggio.";
            return;
        }

        try
        {
            IsBusy = true;
            AiStatus = improve ? "Miglioramento in corso..." : "Generazione in corso...";

            var diff = await _svnService.GetDiffForAiAsync(paths, workingCopy, ct);
            if (!improve && string.IsNullOrWhiteSpace(diff))
            {
                AiStatus = "Nessun diff disponibile.";
                return;
            }

            await InitializeAsync(ct);
            var language = LanguageForAi;
            Text = improve
                ? await ai.ImproveCommitMessageAsync(Text, diff, language, ct)
                : await ai.GenerateCommitMessageAsync(diff, language, ct);

            var provider = DescribeProvider(ai);
            var done = improve ? "Messaggio migliorato" : "Messaggio generato con AI";
            AiStatus = provider is null ? $"{done}." : $"{done} ({provider}).";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AiStatus = $"Errore AI: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Nome del provider che ha risposto, se ricavabile a costo zero.</summary>
    private static string? DescribeProvider(IAiService service) => service switch
    {
        FallbackAiService fallback => fallback.LastUsedProvider,
        CopilotAiService => "GitHub Copilot",
        ClaudeCodeAiService => "Claude Code",
        AnthropicAiService => "Claude API",
        AiService => "OpenAI-compatibile",
        _ => null,
    };
}
