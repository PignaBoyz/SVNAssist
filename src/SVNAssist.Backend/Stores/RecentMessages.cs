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

    public RecentMessages(string? file = null)
        => _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "recent-messages.json");

    /// <summary>Messaggi del repository (il piu' recente per primo); vuoto se non ce ne sono o il file e' illeggibile.</summary>
    internal IReadOnlyList<string> Get(string repo)
    {
        lock (_lock)
            return Read().TryGetValue(repo, out var list) ? list : [];
    }

    /// <summary>Ricorda un messaggio committato (testo tagliato ai bordi; vuoto = ignorato).</summary>
    internal void Add(string repo, string message)
    {
        message = message.Trim();
        if (repo.Length == 0 || message.Length == 0)
            return;
        lock (_lock)
        {
            try
            {
                var all = Read();
                all[repo] = (all.TryGetValue(repo, out var cur) ? cur : []).Where(m => m != message).Prepend(message).Take(Max).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(all));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                BackendLog.Warn("Messaggi recenti non salvati: " + ex.Message);
            }
        }
    }

    private Dictionary<string, List<string>> Read()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return []; // file assente o rovinato: si riparte da zero
        }
    }
}
