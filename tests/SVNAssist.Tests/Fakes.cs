using SVNAssist.Models;
using SVNAssist.Rpc;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Impostazioni finte con valori modificabili (condivise dai test degli store).</summary>
internal sealed class StubSettings : ISvnAssistSettings
{
    public string Provider { get; set; } = "auto";
    public string AnthropicKey { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://models.github.ai/inference/chat/completions";
    public string Language { get; set; } = "auto";
    public string WorkingCopy { get; set; } = string.Empty;
    public string Theme { get; set; } = "auto";

    public Task<string> GetAiProviderAsync(CancellationToken ct) => Task.FromResult(Provider);
    public Task<string> GetClaudeModelAsync(CancellationToken ct) => Task.FromResult("claude");
    public Task<string> GetAnthropicApiKeyAsync(CancellationToken ct) => Task.FromResult(AnthropicKey);
    public Task<string> GetCopilotModelAsync(CancellationToken ct) => Task.FromResult(string.Empty);
    public Task<string> GetAiEndpointAsync(CancellationToken ct) => Task.FromResult(Endpoint);
    public Task<string> GetAiApiKeyAsync(CancellationToken ct) => Task.FromResult(ApiKey);
    public Task<string> GetAiModelAsync(CancellationToken ct) => Task.FromResult("model");
    public Task<string> GetDefaultLanguageAsync(CancellationToken ct) => Task.FromResult(Language);
    public Task<string> GetWorkingCopyPathAsync(CancellationToken ct) => Task.FromResult(WorkingCopy);
    public Task<string> GetWindowThemeAsync(CancellationToken ct) => Task.FromResult(Theme);
    public Task SetWindowThemeAsync(string mode, CancellationToken ct) { Theme = mode; return Task.CompletedTask; }

    /// <summary>Impostazioni salvate con SetSettingAsync (le chiavi AI aggiornano anche i valori letti).</summary>
    public Dictionary<string, string> Saved { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task SetSettingAsync(string key, string value, CancellationToken ct)
    {
        Saved[key] = value;
        switch (key)
        {
            case SettingDefinitions.AiProvider: Provider = value; break;
            case SettingDefinitions.AnthropicApiKey: AnthropicKey = value; break;
            case SettingDefinitions.AiApiKey: ApiKey = value; break;
            case SettingDefinitions.AiEndpoint: Endpoint = value; break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Servizio SVN finto: restituisce stati preimpostati e registra le chiamate.</summary>
internal sealed class FakeSvnService : ISvnService
{
    public List<SvnFileStatus> Statuses { get; } = [];
    public List<IReadOnlyList<string>> Added { get; } = [];
    public List<IReadOnlyList<string>> Deleted { get; } = [];
    public List<IReadOnlyList<string>> Reverted { get; } = [];
    public List<(IReadOnlyList<string> Paths, string Message)> Commits { get; } = [];
    public List<(string Path, SvnConflictChoice Choice)> Resolved { get; } = [];
    public List<string> Ignored { get; } = [];
    public List<string> FileDiffs { get; } = [];
    public List<IReadOnlyList<string>> AiDiffRequests { get; } = [];
    public int StatusCalls { get; private set; }
    public int BranchCalls { get; private set; }
    public int LogRequestedLimit { get; private set; }
    public int LogEntryCount { get; set; } = 500;

    public Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct)
    {
        StatusCalls++;
        if (StatusError is not null)
            throw new InvalidOperationException(StatusError);
        return Task.FromResult<IReadOnlyList<SvnFileStatus>>(Statuses.ToList());
    }

    public string AiDiff { get; set; } = "@@ diff @@";
    public List<string> CommitWholeFolders { get; } = [];
    public List<bool> DeleteForced { get; } = [];
    public string? CommitError { get; set; }
    public string? StatusError { get; set; }
    /// <summary>Se impostato, il commit resta in attesa finché non lo si completa (per provare doppio click e refresh in corso).</summary>
    public TaskCompletionSource? CommitGate { get; set; }

    public async Task<long?> CommitAsync(
        IEnumerable<string> paths, string message, CancellationToken ct, IEnumerable<string>? includeAllUnder = null)
    {
        if (CommitGate is not null)
#pragma warning disable VSTHRD003 // il gate è di proprietà del test
            await CommitGate.Task;
#pragma warning restore VSTHRD003

        if (CommitError is not null)
            throw new InvalidOperationException(CommitError);
        CommitWholeFolders.AddRange(includeAllUnder ?? []);
        Commits.Add((paths.ToList(), message));
        return 42;
    }

    public Task AddAsync(IEnumerable<string> paths, CancellationToken ct) { Added.Add(paths.ToList()); return Task.CompletedTask; }
    public Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct, bool force = false) { Deleted.Add(paths.ToList()); DeleteForced.Add(force); return Task.CompletedTask; }
    public List<bool> RevertRecursive { get; } = [];
    public Task RevertAsync(IEnumerable<string> paths, CancellationToken ct, bool recursive = true) { Reverted.Add(paths.ToList()); RevertRecursive.Add(recursive); return Task.CompletedTask; }
    public Task ResolveAsync(string path, SvnConflictChoice accept, CancellationToken ct) { Resolved.Add((path, accept)); return Task.CompletedTask; }
    public Task AddToIgnoreListAsync(string path, CancellationToken ct) { Ignored.Add(path); return Task.CompletedTask; }
    public Task<UpdateResult> UpdateAsync(string workingCopyPath, CancellationToken ct) => Task.FromResult(new UpdateResult(7, []));

    public Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct)
        => GetLogAsync(targetPath, maxEntries, new SvnLogFilter(), ct);

    public SvnLogFilter? LastLogFilter { get; private set; }
    public List<SvnLogPath> LogPaths { get; } = [];

    public Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, SvnLogFilter filter, CancellationToken ct)
    {
        LogRequestedLimit = maxEntries;
        LastLogFilter = filter;
        IReadOnlyList<SvnLogEntry> list = Enumerable.Range(1, Math.Min(maxEntries, LogEntryCount))
            .Select(i => new SvnLogEntry(i, "u", DateTimeOffset.UnixEpoch, $"m{i}", LogPaths.ToList())).ToList();
        return Task.FromResult(list);
    }

    public Task<string> GetRepositoryRootAsync(string target, CancellationToken ct) => Task.FromResult("file:///repo");

    public List<(string Target, long Rev, string Destination)> ExportedRevisions { get; } = [];

    public Task ExportRevisionAsync(string target, long revision, string destination, CancellationToken ct)
    {
        ExportedRevisions.Add((target, revision, destination));
        File.WriteAllText(destination, $"{target}@{revision}");
        return Task.CompletedTask;
    }

    public List<SvnChange> Changed { get; } = [];

    public Task<IReadOnlyList<SvnChange>> GetChangedBetweenAsync(string target, long fromRevision, long toRevision, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SvnChange>>(Changed.ToList());

    public Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct) => Task.FromResult("diff");

    public Task<string> GetDiffForAiAsync(IReadOnlyList<string> paths, string? workingCopyPath, CancellationToken ct)
    {
        AiDiffRequests.Add(paths);
        return Task.FromResult(AiDiff);
    }

    public Task<string> GetFileDiffAsync(string filePath, CancellationToken ct)
    {
        FileDiffs.Add(filePath);
        return Task.FromResult($"diff of {Path.GetFileName(filePath)}");
    }

    public List<(string Path, string Destination)> Exported { get; } = [];

    public Task ExportBaseAsync(string path, string destination, CancellationToken ct)
    {
        Exported.Add((path, destination));
        File.WriteAllText(destination, "BASE di " + Path.GetFileName(path));
        return Task.CompletedTask;
    }

    public Task<bool> OpenNativeDiffAsync(string filePath, CancellationToken ct) => Task.FromResult(false);

    public Task<string> GetBranchNameAsync(string workingCopyPath, CancellationToken ct)
    {
        BranchCalls++;
        return Task.FromResult("trunk");
    }
}

/// <summary>Dialoghi finti: risposta preimpostata e registro dei messaggi mostrati.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public SvnConflictChoice? ConflictResult { get; set; } = SvnConflictChoice.TheirsFull;
    public List<string> Messages { get; } = [];

    public Task<bool> ConfirmAsync(string message, CancellationToken ct)
    {
        Messages.Add(message);
        return Task.FromResult(ConfirmResult);
    }

    public Task<SvnConflictChoice?> ChooseConflictResolutionAsync(string fileName, CancellationToken ct)
        => Task.FromResult(ConflictResult);
}

/// <summary>Factory AI finta che restituisce un servizio che registra le lingue ricevute.</summary>
internal sealed class FakeAiFactory : IAiServiceFactory
{
    public RecordingAi Service { get; } = new();
    public bool Available { get; set; } = true;
    public IAiService? Custom { get; set; }

    public Task<IAiService?> CreateAsync(CancellationToken ct)
        => Task.FromResult<IAiService?>(Available ? (Custom ?? Service) : null);
}

internal sealed class RecordingAi : IAiService
{
    public List<string> Languages { get; } = [];
    public List<string> Diffs { get; } = [];
    public List<string?> Instructions { get; } = [];

    public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null)
    {
        Diffs.Add(diff);
        Instructions.Add(projectInstructions);
        Languages.Add(language);
        return Task.FromResult("messaggio generato");
    }

    public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null)
    {
        Languages.Add(language);
        return Task.FromResult("migliorato: " + draft);
    }
}

internal sealed class NoopAiPrompt : IAiSetupPrompt
{
    public int Calls { get; private set; }

    public Task<bool> ShowMissingTokenHelpAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(true);
    }
}
