using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SVNAssist.Stores;

/// <summary>Una riga di notifica di svn: <c>K</c> = U/A/D/G/R (azione), C (conflitto), i (informazione, non si conta).</summary>
internal sealed record ProgressLineDto(string K, string T);

/// <summary>Pulsante d'azione della finestra di avanzamento dopo un annullamento: <c>Cmd</c> e' un comando UI (es. <c>switchResume</c>).</summary>
internal sealed record ProgressActionDto(string Label, string Cmd);

/// <summary>
/// Finestra di avanzamento modale (Switch, Merge, Update, Cleanup). <c>Total</c> manca: svn non lo dice, la UI mostra una barra
/// indeterminata e il contatore <c>Processed</c>. Resta aperta a fine operazione (<c>Running</c> falso) con il riepilogo.
/// </summary>
/// <param name="Op"><c>switch</c> | <c>merge</c> | <c>update</c> | <c>cleanup</c>.</param>
/// <param name="Cancellable">Mostra "Annulla": solo switch, merge e update, e solo mentre gira (il cleanup interrotto peggiora la working copy).</param>
/// <param name="Cancelling">Annullamento richiesto, svn in chiusura: il pulsante si disabilita.</param>
/// <param name="Cancelled">Fine per annullamento dell'utente (<c>Ok</c> falso); <c>Actions</c> sono i passi proposti.</param>
/// <param name="Omitted">Righe piu' vecchie non piu' in <c>Lines</c> (si tengono le ultime <see cref="OperationProgress.MaxLines"/>).</param>
internal sealed record ProgressDto(
    string Op, string Title, string Phase, bool Running, bool Ok, int Processed, int Conflicts, int Omitted,
    IReadOnlyList<ProgressLineDto> Lines, string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ConflictPaths = null,
    bool Cancellable = false, bool Cancelling = false, bool Cancelled = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ProgressActionDto>? Actions = null);

/// <summary>Stato condiviso della finestra di avanzamento: lo scrive l'operazione (anche da thread di svn), lo legge lo snapshot di Changes e di Log.</summary>
internal sealed partial class OperationProgress
{
    internal const int MaxLines = 300;
    private const int MaxConflictPaths = 50;

    private readonly object _lock = new();
    private readonly List<ProgressLineDto> _lines = [];
    private readonly List<string> _conflicts = [];
    private string _op = string.Empty, _title = string.Empty, _phase = string.Empty, _summary = string.Empty;
    private bool _open, _running, _ok, _cancelling, _cancelled;
    private IReadOnlyList<ProgressActionDto>? _actions;
    private CancellationTokenSource? _cts;
    private int _processed, _conflictCount, _omitted;

    public event Action? Changed;

    internal bool IsOpen { get { lock (_lock) return _open; } }

    /// <summary>Apre la finestra. Restituisce il token da passare a svn: si annulla con <see cref="Cancel"/> oltre che con <paramref name="ct"/>.</summary>
    internal CancellationToken Begin(string op, string title, string phase, CancellationToken ct = default)
    {
        CancellationToken token;
        lock (_lock)
        {
            (_open, _running, _ok, _op, _title, _phase, _summary) = (true, true, false, op, title, phase, string.Empty);
            (_processed, _conflictCount, _omitted) = (0, 0, 0);
            (_cancelling, _cancelled, _actions) = (false, false, null);
            _lines.Clear();
            _conflicts.Clear();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            token = _cts.Token;
        }

        Changed?.Invoke();
        return token;
    }

    private static bool IsCancellableOp(string op) => op is "switch" or "merge" or "update";

    /// <summary>"Annulla": ferma svn (il runner termina il processo con l'albero). Ignorato se non cancellabile o gia' richiesto.</summary>
    internal bool Cancel()
    {
        lock (_lock)
        {
            if (!_running || _cancelling || !IsCancellableOp(_op) || _cts is null)
                return false;
            _cancelling = true;
            _cts.Cancel();
        }

        Changed?.Invoke();
        return true;
    }

    internal void SetPhase(string phase)
    {
        lock (_lock)
            _phase = phase;
        Changed?.Invoke();
    }

    /// <summary>Riga di stdout di svn (da qualunque thread).</summary>
    internal void Line(string raw)
    {
        var (kind, path) = Classify(raw);
        if (raw.Trim().Length == 0)
            return;
        lock (_lock)
        {
            if (!_open)
                return;
            if (kind != "i")
                _processed++;
            if (kind == "C")
            {
                _conflictCount++;
                if (_conflicts.Count < MaxConflictPaths)
                    _conflicts.Add(path);
            }

            _lines.Add(new ProgressLineDto(kind, raw.TrimEnd()));
            if (_lines.Count > MaxLines)
            {
                _lines.RemoveRange(0, _lines.Count - MaxLines);
                _omitted++;
            }
        }

        Changed?.Invoke();
    }

    internal void Done(bool ok, string summary, int? conflicts = null, bool cancelled = false, IReadOnlyList<ProgressActionDto>? actions = null)
    {
        lock (_lock)
        {
            (_running, _ok, _summary, _phase, _conflictCount) = (false, ok, summary, string.Empty, conflicts ?? _conflictCount);
            (_cancelling, _cancelled, _actions) = (false, cancelled, actions);
        }

        Changed?.Invoke();
    }

    internal void Close()
    {
        lock (_lock)
            _open = false;
        Changed?.Invoke();
    }

    internal int ConflictCount { get { lock (_lock) return _conflictCount; } }

    internal bool IsCancelling { get { lock (_lock) return _cancelling; } }

    internal int Processed { get { lock (_lock) return _processed; } }

    internal ProgressDto? GetSnapshot()
    {
        lock (_lock)
            return !_open ? null
                : new ProgressDto(_op, _title, _phase, _running, _ok, _processed, _conflictCount, _omitted, _lines.ToList(), _summary, _conflicts.Count > 0 ? _conflicts.ToList() : null,
                    _running && IsCancellableOp(_op), _cancelling, _cancelled, _actions);
    }

    /// <summary>
    /// Classifica una riga: <c>U    path</c> (4 colonne di stato + percorso, anche <c>D         path</c> del cleanup), <c>Reverted 'path'</c>,
    /// <c>Restored 'path'</c>; <c>C</c> in una colonna = conflitto. Tutto il resto (<c>Updating '.':</c>, <c>--- Merging ...</c>, riepiloghi) e' informazione.
    /// </summary>
    internal static (string Kind, string Path) Classify(string raw)
    {
        var m = ActionLine().Match(raw);
        if (m.Success && m.Groups["cols"].Value.Trim().Length > 0)
        {
            var c = m.Groups["cols"].Value;
            return (c.Contains('C') ? "C" : c[0] is 'A' or 'D' or 'G' or 'R' ? c[0].ToString() : "U", m.Groups["path"].Value);
        }

        var r = Reverted().Match(raw);
        return r.Success ? ("R", r.Groups["path"].Value) : ("i", string.Empty);
    }

    [GeneratedRegex(@"^(?<cols>[ ADUCGER][ UCGM][ B][ C])\s+(?<path>\S.*?)\s*$")]
    private static partial Regex ActionLine();

    [GeneratedRegex(@"^(?:Reverted|Restored) '(?<path>.*)'\s*$")]
    private static partial Regex Reverted();
}
