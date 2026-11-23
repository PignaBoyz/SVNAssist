using System.IO;
using System.Text.Json;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Ultimi messaggi di commit per repository (tendina "Recenti", come TortoiseSVN): al massimo <see cref="Max"/>, il piu' recente per primo,
/// senza doppioni (un messaggio gia' presente risale in cima). Un file JSON <c>{ "repo": ["msg", ...] }</c> in
/// <c>%LOCALAPPDATA%\SVNAssist\recent-messages.json</c>; best effort: se non si puo' scrivere il commit non ne risente.
/// </summary>
internal sealed class RecentMessages
{
    internal const int Max = 25;

    private readonly string _file;
    private readonly object _lock = new();
    private Dictionary<string, List<string>>? _cache;
    private DateTime _cacheStampUtc;
    private long _cacheLen = -1;

    public RecentMessages(string? file = null)
        => _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "recent-messages.json");

    /// <summary>Messaggi del repository (il piu' recente per primo); vuoto se non ce ne sono o il file e' illeggibile.</summary>
    internal IReadOnlyList<string> Get(string repo)
    {
        lock (_lock)
            return AllCached().TryGetValue(repo, out var list) ? Normalize(list) : [];
    }

    /// <summary>Ricorda un messaggio committato (testo tagliato ai bordi; vuoto = ignorato).</summary>
    internal void Add(string repo, string message)
    {
        message = AiServiceBase.CleanAnswer(message);
        if (repo.Length == 0 || message.Length == 0)
            return;
        lock (_lock)
        {
            try
            {
                AtomicJsonFile.WithFileLock(_file, () =>
                {
                    var all = Read();
                    all[repo] = Normalize(all.TryGetValue(repo, out var cur) ? cur : []).Where(m => m != message).Prepend(message).Take(Max).ToList();
                    AtomicJsonFile.WriteAllTextAtomic(_file, JsonSerializer.Serialize(all));
                    _cache = all;
                    if (File.Exists(_file))
                    {
                        var fi = new FileInfo(_file);
                        _cacheStampUtc = fi.LastWriteTimeUtc;
                        _cacheLen = fi.Length;
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BackendLog.Warn("Messaggi recenti non salvati: " + ex.Message);
            }
        }
    }

    private Dictionary<string, List<string>> AllCached()
    {
        try
        {
            if (!File.Exists(_file))
            {
                _cacheStampUtc = default;
                _cacheLen = -1;
                return _cache = [];
            }

            var fi = new FileInfo(_file);
            if (_cache is not null && fi.LastWriteTimeUtc == _cacheStampUtc && fi.Length == _cacheLen)
                return _cache;

            _cacheStampUtc = fi.LastWriteTimeUtc;
            _cacheLen = fi.Length;
            return _cache = Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _cache ??= [];
        }
    }

    /// <summary>Ripulisce le voci gia' salvate (es. messaggi AI in un blocco ```): normalizza, scarta le vuote, toglie i doppioni.</summary>
    private static List<string> Normalize(IEnumerable<string> list)
        => list.Select(AiServiceBase.CleanAnswer).Where(m => m.Length > 0).Distinct().ToList();

    private Dictionary<string, List<string>> Read()
    {
        try
        {
            var json = AtomicJsonFile.TryReadAllText(_file);
            return json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return []; // file assente o rovinato: si riparte da zero
        }
    }
}
