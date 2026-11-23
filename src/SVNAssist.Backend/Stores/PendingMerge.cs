using System.Text.Json;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Merge di piu' revisioni non concluso, per working copy (file JSON, sopravvive al riavvio di VS; modello: <see cref="PendingSwitch"/>).
/// Si scrive prima del primo <c>svn merge</c>, si aggiorna a ogni passaggio e si cancella quando il merge e' concluso (tutte le revisioni applicate),
/// annullato, committato o scartato. Se resta, il merge e' a meta': le revisioni <see cref="Entry.Applied"/> sono nella working copy (e in <c>svn:mergeinfo</c>),
/// le altre si applicano con "Continua merge" senza rifare la scelta.
/// </summary>
internal sealed class PendingMerge
{
    /// <summary>Stati: <c>running</c> (in corso: se resta cosi' dopo un riavvio, VS si e' chiuso a meta'), <c>stopped</c> (fermo per un conflitto), <c>interrupted</c> (fermato dall'utente o da un errore).</summary>
    internal const string Running = "running", Stopped = "stopped", Interrupted = "interrupted";

    /// <param name="Name">Sorgente come nel selettore (<c>branches/x</c>, con <c>/sottopercorso</c> per il merge di una cartella).</param>
    /// <param name="Url">URL usato da <c>svn merge</c> (per una cartella e' l'URL del sottopercorso).</param>
    /// <param name="Path">Percorso nel repository, senza <c>^/</c> (per il messaggio di commit e i file per revisione).</param>
    /// <param name="Wc">Cartella su cui gira il merge: la radice della working copy, o la cartella scelta.</param>
    /// <param name="Sub">Sottopercorso relativo (<c>/</c>) della cartella, null = intera working copy.</param>
    /// <param name="WcUrl">URL e revisione della working copy al via (<c>svn info</c>): se cambiano (switch, update) il merge non e' piu' proseguibile.</param>
    /// <param name="Revs">Tutte le revisioni del merge, crescenti: la numerazione "3 di 7" si riferisce a questa lista.</param>
    /// <param name="Applied">Revisioni applicate (conflitto compreso).</param>
    /// <param name="ConflictRev">Revisione dopo cui si e' fermato per conflitto; <see cref="ConflictFrom"/>-<see cref="ConflictTo"/> se il conflitto sta dentro un intervallo applicato in un colpo.</param>
    /// <param name="ConflictFiles">File in conflitto al momento dello stop.</param>
    /// <param name="InFlight">Passaggio che era in corso quando il processo e' stato terminato a forza (puo' essere applicato a meta').</param>
    /// <param name="Backup">Cartella della copia di sicurezza delle modifiche pendenti di prima del merge (vuota se la working copy era pulita).</param>
    /// <param name="Reason">Perche' si e' interrotto (testo di errore o motivo), per il messaggio.</param>
    /// <param name="Unrecorded">Revisioni applicate che <c>svn mergeinfo</c> continuava a dare come da unire al momento dello stop (svn non le ha registrate): non sono un segno di working copy modificata.</param>
    internal sealed record Entry(
        string Name, string Url, string Path, string Wc, string? Sub, string? WcUrl, long? WcRev,
        List<long> Revs, List<long> Applied, string State, DateTime Started, DateTime Updated,
        long? ConflictRev = null, long? ConflictFrom = null, long? ConflictTo = null, int ConflictFiles = 0,
        long? InFlightFrom = null, long? InFlightTo = null, string? Backup = null, string? Reason = null, List<long>? Unrecorded = null)
    {
        internal List<long> Remaining => Revs.Except(Applied).Order().ToList();
    }

    private readonly string? _file;
    private readonly object _lock = new();
    private Dictionary<string, Entry>? _all;
    private DateTime _cacheStampUtc;
    private long _cacheLen = -1;

    /// <param name="file">File JSON; <c>null</c> = solo in memoria (test e prove: non scrive in <c>%LOCALAPPDATA%</c>).</param>
    public PendingMerge(string? file = null) => _file = file;

    /// <summary>Percorso predefinito: <c>%LOCALAPPDATA%\SVNAssist\pending-merge.json</c>.</summary>
    internal static string DefaultFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "pending-merge.json");

    internal Entry? Get(string workingCopy)
    {
        if (string.IsNullOrWhiteSpace(workingCopy))
            return null;
        lock (_lock)
            return All().GetValueOrDefault(Key(workingCopy));
    }

    internal void Set(string workingCopyRoot, Entry entry) => Change(all => all[Key(workingCopyRoot)] = entry with { Updated = DateTime.Now });

    internal void Clear(string workingCopyRoot)
    {
        if (Get(workingCopyRoot) is not null)
            Change(all => all.Remove(Key(workingCopyRoot)));
    }

    private static string Key(string workingCopy) => System.IO.Path.GetFullPath(workingCopy).TrimEnd('\\', '/').ToUpperInvariant();

    private void Change(Action<Dictionary<string, Entry>> edit)
    {
        lock (_lock)
        {
            if (_file is null)
            {
                edit(_all ??= []);
                return;
            }

            try
            {
                AtomicJsonFile.WithFileLock(_file, () =>
                {
                    var all = Read();
                    edit(all);
                    AtomicJsonFile.WriteAllTextAtomic(_file, JsonSerializer.Serialize(all));
                    _all = all;
                    UpdateStamp();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BackendLog.Warn("Stato del merge non salvato: " + ex.Message);
            }
        }
    }

    private Dictionary<string, Entry> All()
    {
        if (_file is null)
            return _all ??= [];
        try
        {
            if (!File.Exists(_file))
            {
                _cacheStampUtc = default;
                _cacheLen = -1;
                return _all = [];
            }

            var fi = new FileInfo(_file);
            if (_all is not null && fi.LastWriteTimeUtc == _cacheStampUtc && fi.Length == _cacheLen)
                return _all;

            _cacheStampUtc = fi.LastWriteTimeUtc;
            _cacheLen = fi.Length;
            return _all = Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _all ??= [];
        }
    }

    private void UpdateStamp()
    {
        try
        {
            if (_file is not null && File.Exists(_file))
            {
                var fi = new FileInfo(_file);
                _cacheStampUtc = fi.LastWriteTimeUtc;
                _cacheLen = fi.Length;
            }
        }
        catch { /* best effort */ }
    }

    private Dictionary<string, Entry> Read()
    {
        if (_file is null)
            return [];
        try
        {
            var json = AtomicJsonFile.TryReadAllText(_file);
            return json is null
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
