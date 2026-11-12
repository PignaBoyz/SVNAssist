using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVNAssist.Stores;

// DTO degli snapshot: devono restare conformi a src/SVNAssist.UI.Web/contract/*.schema.json
// (verificato dai test SnapshotSchemaTests). Proprieta' camelCase; i campi facoltativi null non vengono scritti.

internal sealed record WorkingCopyDto(string Path, bool ShowInput, string Branch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Rev = null);

internal sealed record TraceDto(string Name, string State, string Detail);

internal sealed record DownloadDto(double Pct, string Label);

/// <param name="AiLevel">Gravita' di <c>AiStatus</c> per la UI: busy | ok | warn | err | info (vuoto se nessun messaggio).</param>
/// <param name="Count">Elementi del prossimo commit: quelli in Staged, oppure (Staged vuoto) tutte le modifiche dei file versionati.</param>
/// <param name="All">true se Staged e' vuoto e il commit prende tutte le modifiche (pulsante "Commit All").</param>
/// <param name="AiSeq">Cresce a ogni messaggio AI: la UI lo usa per rendere visibile anche un messaggio ripetuto.</param>
internal sealed record CommitDto(string Text, string AiStatus, string AiLevel, int AiSeq, bool Busy, string Lang, IReadOnlyList<TraceDto> Trace, DownloadDto? Download, int Count = 0, bool All = false);

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

internal sealed record StatusDto(string Msg, bool Err, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StatusActionDto? Action = null);

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
    RevPickerDto RevPicker);

/// <param name="K"><c>dir</c> per le cartelle (non si aprono nel diff); assente altrimenti.</param>
internal sealed record LogPathDto(string A, string P, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? K = null);

/// <summary>Filtri del log come scritti dall'utente (date <c>aaaa-mm-gg</c>).</summary>
internal sealed record LogFilterDto(string Author, string Text, string Path, string From, string To);

/// <summary>Elenco di file con diff a due pannelli: <c>folder</c> (modifiche di una cartella) o <c>compare</c> (due revisioni).</summary>
internal sealed record LogFilesDto(string Kind, string Title, IReadOnlyList<LogPathDto> Items);

internal sealed record LogEntryDto(long Rev, string Author, string Date, string Msg, IReadOnlyList<LogPathDto> Paths);

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
    LogFilesDto? Files);

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
