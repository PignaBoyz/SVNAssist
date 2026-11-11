using SVNAssist.Rpc;

namespace SVNAssist.Tests;

/// <summary>Host finto: registra dialoghi, diff aperti e messaggi spediti alla UI; le risposte ai dialoghi sono preimpostate.</summary>
internal sealed class FakeHost : IHost
{
    public List<(string View, string Json)> Pushed { get; } = [];
    public List<(string Message, string[] Buttons)> Prompts { get; } = [];
    public List<(string Left, string Right, string LeftLabel, string RightLabel, string Caption)> Diffs { get; } = [];
    public List<string> OpenedFiles { get; } = [];
    public List<string> Urls { get; } = [];
    public List<(string Command, string Dir)> Terminal { get; } = [];
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Key, string Value)> SettingWrites { get; } = [];
    public Queue<int> PromptAnswers { get; } = new();

    /// <summary>Risposta quando la coda e' vuota (0 = primo pulsante, cioe' "OK").</summary>
    public int DefaultAnswer { get; set; }

    public Task PushAsync(string view, string messageJson)
    {
        lock (Pushed)
            Pushed.Add((view, messageJson));
        return Task.CompletedTask;
    }

    public Task<int> PromptAsync(string message, string[] buttons)
    {
        Prompts.Add((message, buttons));
        return Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : DefaultAnswer);
    }

    public Task<Dictionary<string, string>> GetSettingsAsync() => Task.FromResult(new Dictionary<string, string>(Settings));

    public Task SetSettingAsync(string key, string value)
    {
        SettingWrites.Add((key, value));
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url)
    {
        Urls.Add(url);
        return Task.CompletedTask;
    }

    public Task OpenDiffAsync(string leftPath, string rightPath, string leftLabel, string rightLabel, string caption)
    {
        Diffs.Add((leftPath, rightPath, leftLabel, rightLabel, caption));
        return Task.CompletedTask;
    }

    public Task OpenFileAsync(string path)
    {
        OpenedFiles.Add(path);
        return Task.CompletedTask;
    }

    public Task ShowLogAsync(string path) => Task.CompletedTask;

    public Task RunInTerminalAsync(string command, string workingDir)
    {
        Terminal.Add((command, workingDir));
        return Task.CompletedTask;
    }
}
