using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVNAssist.Stores;

// DTO degli snapshot: devono restare conformi a src/SVNAssist.UI.Web/contract/*.schema.json
// (verificato dai test SnapshotSchemaTests). Proprieta' camelCase; i campi facoltativi null non vengono scritti.

internal sealed record WorkingCopyDto(string Path, bool ShowInput, string Branch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Rev = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SvnVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Head = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RevState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RevNote = null);

internal sealed record TraceDto(string Name, string State, string Detail);

internal sealed record DownloadDto(double Pct, string Label);

/// <param name="AiLevel">Gravita' di <c>AiStatus</c> per la UI: busy | ok | warn | err | info (vuoto se nessun messaggio).</param>
/// <param name="Count">Elementi del prossimo commit: quelli in Staged, oppure (Staged vuoto) tutte le modifiche dei file versionati.</param>
/// <param name="All">true se Staged e' vuoto e il commit prende tutte le modifiche (pulsante "Commit All").</param>
/// <param name="AiSeq">Cresce a ogni messaggio AI: la UI lo usa per rendere visibile anche un messaggio ripetuto.</param>
internal sealed record CommitDto(string Text, string AiStatus, string AiLevel, int AiSeq, bool Busy, string Lang, IReadOnlyList<TraceDto> Trace, DownloadDto? Download, int Count = 0, bool All = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Recent = null);

internal sealed record ThemeDto(string Mode, string Kind);

internal sealed record CanDto(bool Diff, bool Stage, bool Unstage, bool Add, bool Ignore, bool Revert, bool Resolve, bool DeleteFile, bool Lock = false, bool Unlock = false);

internal sealed record RowDto(
    string Key,
    string Kind,
    int Depth,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Dir,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Expanded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count,
    string? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StatusText,
    CanDto Can,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tooltip,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Locked = null);

internal sealed record SectionDto(string Id, string Title, int Count, bool Expanded, IReadOnlyList<RowDto> Rows);

/// <summary>Pulsante d'azione accanto al messaggio di stato (es. "Esegui Cleanup" quando la working copy è bloccata).</summary>
internal sealed record StatusActionDto(string Label, string Cmd);

internal sealed record StatusDto(string Msg, bool Err, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StatusActionDto? Action = null, int Seq = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StatusActionDto? Action2 = null);

internal sealed record PrereqItemDto(string Id, string Group, string Title, bool Ok, string Detail, string Instructions, string Primary, string Secondary);

internal sealed record PrereqDto(bool Open, string Summary, string Ai, string Feedback, IReadOnlyList<PrereqItemDto> Items);

internal sealed record WizardProviderDto(string Id, string Title, string Cost);

internal sealed record WizardModelDto(string Value, string Label);

/// <summary>Procedura guidata "Configura l'AI": passo 1 provider, 2 prerequisiti, 3 accesso, 4 modello.</summary>
internal sealed record WizardDto(
    bool Open,
    string Mode,
    int Step,
    bool FromGenerate,
    string Provider,
    bool Busy,
    string Feedback,
    IReadOnlyList<WizardProviderDto> Providers,
    IReadOnlyList<PrereqItemDto> Items,
    string Auth,
    bool AuthOk,
    string AuthDetail,
    string Endpoint,
    IReadOnlyList<WizardModelDto> Models,
    string Model,
    bool CanNext);

internal sealed record RevItemDto(long Rev, string Author, string Date, string Msg);

/// <summary>Scelta guidata della revisione per "Update to revision": <c>Target</c> e' il percorso da aggiornare, <c>Error</c> un numero non valido.</summary>
internal sealed record RevPickerDto(bool Open, string Target, bool Loading, string Error, bool CanMore, IReadOnlyList<RevItemDto> Items);

/// <param name="Kind"><c>trunk</c> | <c>branch</c> | <c>tag</c>.</param>
/// <param name="Rev">Ultima revisione che ha toccato il ramo (0 = sconosciuta).</param>
/// <param name="Date">ISO 8601, vuota se sconosciuta.</param>
internal sealed record SwitchItemDto(string Name, string Kind, bool Current, long Rev, string Author, string Date, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Incomplete = null);

/// <summary>Selettore di "SVN Switch...": <c>Name</c> degli elementi e' relativo alla base (<c>trunk</c>, <c>branches/x</c>, <c>tags/1.0</c>).</summary>
/// <param name="Mode"><c>switch</c> (SVN Switch) o <c>merge</c> (SVN Merge: sorgente da cui unire, senza il ramo corrente).</param>
/// <param name="Recent">Ultimi nomi scelti per questo repository (piu' recente per primo), solo quelli ancora in elenco.</param>
internal sealed record SwitchPickerDto(bool Open, string Mode, bool Loading, string Error, IReadOnlyList<SwitchItemDto> Items, IReadOnlyList<string> Recent);

/// <summary>Un conflitto da risolvere (sezione Conflitti di Changes). <c>Key</c> e' la chiave di riga (<c>changes:percorso</c>).</summary>
/// <param name="Kind"><c>text</c> | <c>property</c> | <c>tree</c>.</param>
/// <param name="Files">File di appoggio di svn (nomi), da <c>svn info --xml</c>: spariscono dopo il resolve.</param>
internal sealed record ConflictDto(string Key, string Path, string Name, string Kind, IReadOnlyList<string> Files);

/// <summary>Banner dopo un merge non ancora committato (con "Annulla merge").</summary>
internal sealed record MergeBannerDto(string Text, int Conflicts);

/// <summary>Snapshot della vista <c>changes</c> (changes-state.schema.json).</summary>
internal sealed record ChangesState(
    WorkingCopyDto WorkingCopy,
    CommitDto Commit,
    string View,
    ThemeDto Theme,
    IReadOnlyList<SectionDto> Sections,
    StatusDto Status,
    PrereqDto Prereq,
    bool Loading,
    WizardDto Wizard,
    RevPickerDto RevPicker,
    SwitchPickerDto SwitchPicker,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MergeBannerDto? Merge = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ConflictDto>? Conflicts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProgressDto? Progress = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SwitchWarnDto? SwitchWarn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CleanupDialogDto? Cleanup = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommitWarnDto? CommitWarn = null);

/// <param name="K"><c>dir</c> per le cartelle (non si aprono nel diff); assente altrimenti.</param>
internal sealed record LogPathDto(string A, string P, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? K = null);

/// <summary>Filtri del log come scritti dall'utente (date <c>aaaa-mm-gg</c>).</summary>
internal sealed record LogFilterDto(string Author, string Text, string Path, string From, string To);

/// <summary>Elenco di file con diff a due pannelli: <c>folder</c> (modifiche di una cartella) o <c>compare</c> (due revisioni).</summary>
internal sealed record LogFilesDto(string Kind, string Title, IReadOnlyList<LogPathDto> Items);

/// <param name="Depth">0 = revisione del log; &gt;0 = entrata per merge (<c>svn log -g</c>), annidata sotto la revisione di merge che la precede.</param>
/// <param name="Reverse"><c>reverse-merge</c>: la revisione e' stata tolta dal merge.</param>
internal sealed record LogEntryDto(long Rev, string Author, string Date, string Msg, IReadOnlyList<LogPathDto> Paths,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Depth = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Reverse = false);

internal sealed record MergeCountsDto(int Updated, int Added, int Deleted, int Merged, int Conflicted);

/// <summary>Esito dell'anteprima (<c>preview</c>) o del merge (<c>merge</c>): conteggi, file e conflitti. <c>Revs</c> = "r120-r135" o "r120, r124".</summary>
internal sealed record MergeResultDto(string Mode, string Revs, MergeCountsDto Counts, IReadOnlyList<LogPathDto> Items, int Skipped);

/// <summary>Il Log in modalita' merge: <c>Selected</c> = revisioni scelte per il cherry-pick; <c>Blocked</c> = perche' non si puo' fare il merge (working copy sporca), vuoto se si puo'.
/// <c>NeedsUpdate</c> = l'errore suggerisce SVN Update (revisioni miste): la UI offre "SVN Update ora".
/// <c>Pending</c> = merge eseguito e non ancora committato ne' annullato (le revisioni "da unire" non vanno mostrate come zero).</summary>
internal sealed record MergeDto(
    string Source,
    string Title,
    bool Loading,
    bool Busy,
    string Error,
    string Blocked,
    IReadOnlyList<long> Selected,
    int Eligible,
    MergeResultDto? Result,
    bool Pending = false,
    bool NeedsUpdate = false);

/// <summary>Snapshot della vista <c>log</c> (log-state.schema.json).</summary>
internal sealed record LogState(
    string Path,
    string Msg,
    bool Err,
    bool Loading,
    bool CanMore,
    long? Sel,
    IReadOnlyList<LogEntryDto> Entries,
    string Diff,
    LogFilterDto Filter,
    LogFilesDto? Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MergeDto? Merge = null,
    bool StopOnCopy = false,
    bool IncludeMerged = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProgressDto? Progress = null);

/// <summary>Riepilogo della working copy sporca prima di uno switch: conteggi per tipo (i non versionati non portano modifiche ma si contano).</summary>
internal sealed record DirtyCountsDto(int Modified, int Added, int Deleted, int Conflicted, int Unversioned);

/// <summary>Avviso prima dello switch su una working copy sporca: Continua comunque / Apri Cleanup / Annulla. <c>Total</c> 0 = ora e' pulita.</summary>
internal sealed record SwitchWarnDto(string Target, DirtyCountsDto Counts, int Total);

/// <summary>Domanda di "Commit All" con file non versionati: Count totali, Items i primi (max 10), More quanti non elencati.</summary>
internal sealed record CommitWarnDto(int Count, IReadOnlyList<string> Items, int More);

/// <summary>Finestra delle opzioni del Cleanup. <c>Extras</c> falso = svn &lt; 1.9: le opzioni oltre il cleanup semplice si disabilitano.</summary>
internal sealed record CleanupDialogDto(bool Extras, string SvnVersion, string From);

/// <summary>Snapshot della vista <c>diff</c> (diff-state.schema.json).</summary>
internal sealed record DiffState(string Title, string Subtitle, string Diff);

/// <summary>Serializzazione JSON del protocollo UI (camelCase, null scritti salvo dove indicato).</summary>
internal static class UiJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Nessun escape \uXXXX inutile: gli accenti restano leggibili nei log di debug.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
