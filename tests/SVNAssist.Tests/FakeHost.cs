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

    public Task SccStatusChangedAsync() => Task.CompletedTask;

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

    /// <summary>Risposte ai dialoghi di testo (revisione, messaggio di lock); coda vuota = <see cref="DefaultText"/>.</summary>
    public Queue<string?> TextAnswers { get; } = new();
    public string? DefaultText { get; set; } = "";
    public List<(string Message, string Label, string Default)> TextPrompts { get; } = [];

    public Task<string?> PromptTextAsync(string message, string label, string defaultValue)
    {
        TextPrompts.Add((message, label, defaultValue));
        return Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : DefaultText);
    }

    public string[]? Credentials { get; set; }
    public int CredentialPrompts { get; private set; }

    public Task<string[]?> PromptCredentialsAsync(string detail, string defaultUser)
    {
        CredentialPrompts++;
        return Task.FromResult(Credentials);
    }

    public Task<Dictionary<string, string>> GetSettingsAsync() => Task.FromResult(new Dictionary<string, string>(Settings));

    public Task SetSettingAsync(string key, string value)
    {
        SettingWrites.Add((key, value));
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public int SettingsOpened { get; private set; }

    public Task OpenSettingsAsync()
    {
        SettingsOpened++;
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

    public bool OpenMergeResult { get; set; }
    public List<(string Left, string Right, string Base, string Result, string LeftLabel, string RightLabel, string BaseLabel, string ResultLabel)> Merges { get; } = [];

    public Task<bool> OpenMergeAsync(string leftPath, string rightPath, string basePath, string resultPath, string leftLabel, string rightLabel, string baseLabel, string resultLabel)
    {
        Merges.Add((leftPath, rightPath, basePath, resultPath, leftLabel, rightLabel, baseLabel, resultLabel));
        return Task.FromResult(OpenMergeResult);
    }

    public Task OpenFileAsync(string path)
    {
        OpenedFiles.Add(path);
        return Task.CompletedTask;
    }

    public List<string> LogShown { get; } = [];
    public Task ShowLogAsync(string path) { LogShown.Add(path); return Task.CompletedTask; }

    public List<string> LogWindowTitles { get; } = [];

    public Task ShowLogWindowAsync(string title) { LogWindowTitles.Add(title); return Task.CompletedTask; }

    /// <summary>Se non nullo, simula un documento aperto: ApplyDocumentTextAsync registra (percorso, testo) e risponde true.</summary>
    public List<(string Path, string Text)>? AppliedTexts { get; set; }

    public Task<bool> ApplyDocumentTextAsync(string path, string text)
    {
        AppliedTexts?.Add((path, text));
        return Task.FromResult(AppliedTexts is not null);
    }

    /// <summary>Percorso scelto nei dialoghi Salva/Apri file (null = annullato); registra (save, titolo).</summary>
    public string? PickedFile { get; set; }
    public List<(bool Save, string Title)> Picks { get; } = [];

    public Task<string?> PickFileAsync(bool save, string title, string defaultName, string filter)
    {
        Picks.Add((save, title));
        return Task.FromResult(PickedFile);
    }

    public int ChangesShown { get; private set; }

    public Task ShowChangesWindowAsync() { ChangesShown++; return Task.CompletedTask; }

    public List<string> FolderDiffs { get; } = [];

    public Task ShowFolderDiffAsync(string path)
    {
        FolderDiffs.Add(path);
        return Task.CompletedTask;
    }

    public Task RunInTerminalAsync(string command, string workingDir)
    {
        Terminal.Add((command, workingDir));
        return Task.CompletedTask;
    }
}
