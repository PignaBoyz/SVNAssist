using System.Text.Json;

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
    private Dictionary<string, Entry>? _all; // letto una volta, poi in memoria (lo snapshot lo chiede a ogni disegno)

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
                var all = All();
                edit(all);
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(all));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BackendLog.Warn("Stato dello switch non salvato: " + ex.Message);
            }
        }
    }

    private Dictionary<string, Entry> All() => _all ??= Read();

    private Dictionary<string, Entry> Read()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
