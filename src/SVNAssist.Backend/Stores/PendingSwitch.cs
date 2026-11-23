using System.Text.Json;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Switch non completato, per working copy (file JSON, sopravvive al riavvio di VS). Si scrive prima di lanciare
/// <c>svn switch</c> e si cancella quando finisce bene: se resta, la working copy e' a meta' tra due rami
/// (la radice e' gia' sul ramo nuovo, alcune cartelle no) e il rimedio e' <c>svn cleanup</c> + lo stesso switch, non il revert.
/// </summary>
internal sealed class PendingSwitch
{
    /// <param name="FromUrl">URL, revisione e nome di partenza (da svn info prima dello switch): servono a "Torna a prima dello switch"; null nelle voci vecchie.</param>
    internal sealed record Entry(string Name, string Url, string? FromUrl = null, long? FromRev = null, string? FromName = null)
    {
        internal bool CanGoBack => !string.IsNullOrEmpty(FromUrl) && FromRev is > 0;
    }

    private readonly string _file;
    private readonly object _lock = new();
    private Dictionary<string, Entry>? _all; // in memoria, invalidato se un'altra istanza VS scrive il file
    private DateTime _cacheStampUtc;
    private long _cacheLen = -1;

    /// <param name="file">Default <c>%LOCALAPPDATA%\SVNAssist\pending-switch.json</c>.</param>
    public PendingSwitch(string? file = null)
        => _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "pending-switch.json");

    internal Entry? Get(string workingCopy)
    {
        if (string.IsNullOrWhiteSpace(workingCopy))
            return null;
        lock (_lock)
            return All().GetValueOrDefault(Key(workingCopy));
    }

    internal void Set(string workingCopy, Entry entry) => Change(all => all[Key(workingCopy)] = entry);

    internal void Clear(string workingCopy)
    {
        if (Get(workingCopy) is not null)
            Change(all => all.Remove(Key(workingCopy)));
    }

    private static string Key(string workingCopy) => Path.GetFullPath(workingCopy).TrimEnd('\\', '/').ToUpperInvariant();

    private void Change(Action<Dictionary<string, Entry>> edit)
    {
        lock (_lock)
        {
            try
            {
                AtomicJsonFile.WithFileLock(_file, () =>
                {
                    var all = Read();
                    edit(all);
                    AtomicJsonFile.WriteAllTextAtomic(_file, JsonSerializer.Serialize(all));
                    _all = all;
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
                BackendLog.Warn("Stato dello switch non salvato: " + ex.Message);
            }
        }
    }

    private Dictionary<string, Entry> All()
    {
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

    private Dictionary<string, Entry> Read()
    {
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
