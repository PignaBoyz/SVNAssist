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
    public string UiLanguage { get; set; } = "it";

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
    public Task<string> GetUiLanguageAsync(CancellationToken ct) => Task.FromResult(UiLanguage);
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
    public Task RevertAsync(IEnumerable<string> paths, CancellationToken ct, bool recursive = true)
    {
        Reverted.Add(paths.ToList());
        RevertRecursive.Add(recursive);
        // Il revert vero fa sparire dallo stato le modifiche dei percorsi indicati (i non versionati restano).
        Statuses.RemoveAll(s => s.Status != SvnStatusKind.Unversioned && Reverted[^1].Contains(s.Path, StringComparer.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }
    /// <summary>Percorsi per cui <see cref="ResolveAsync"/> fallisce (prova della risoluzione di gruppo con un errore in mezzo).</summary>
    public HashSet<string> ResolveFailPaths { get; } = [];
    public Task ResolveAsync(string path, SvnConflictChoice accept, CancellationToken ct)
    {
        if (ResolveFailPaths.Contains(path))
            throw new InvalidOperationException("svn: E155010: boom " + Path.GetFileName(path));
        Resolved.Add((path, accept));
        return Task.CompletedTask;
    }
    public Task AddToIgnoreListAsync(string path, CancellationToken ct) { Ignored.Add(path); return Task.CompletedTask; }
    public int Updates { get; private set; }

    public Task<UpdateResult> UpdateAsync(string workingCopyPath, CancellationToken ct)
    {
        Updates++;
        return UpdateError is not null ? throw new InvalidOperationException(UpdateError) : Task.FromResult(new UpdateResult(7, UpdateConflicts));
    }

    public IReadOnlyList<string> UpdateConflicts { get; set; } = [];

    public List<(string Path, string Revision)> UpdatedToRevision { get; } = [];
    public List<string> CleanedUp { get; } = [];
    public List<(IReadOnlyList<string> Paths, string? Message, bool Force)> Locked { get; } = [];
    public List<IReadOnlyList<string>> Unlocked { get; } = [];
    /// <summary>Se impostato, <see cref="LockAsync"/> fallisce con questo messaggio finché la chiamata non è con force.</summary>
    public string? LockErrorUnlessForced { get; set; }
    public string? UpdateError { get; set; }

    public Task<UpdateResult> UpdateToRevisionAsync(string workingCopyPath, string revision, CancellationToken ct)
    {
        UpdatedToRevision.Add((workingCopyPath, revision));
        return Task.FromResult(new UpdateResult(long.TryParse(revision, out var n) ? n : 9, []));
    }

    public Task CleanupAsync(string path, CancellationToken ct) { CleanedUp.Add(path); SwitchLog.Add("cleanup"); return Task.CompletedTask; }
    public List<CleanupOptions> CleanupOptionsUsed { get; } = [];
    public Task CleanupAsync(string path, CleanupOptions options, CancellationToken ct) { CleanupOptionsUsed.Add(options); return CleanupAsync(path, ct); }

    public Task LockAsync(IEnumerable<string> paths, string? message, bool force, CancellationToken ct)
    {
        if (LockErrorUnlessForced is not null && !force)
            throw new InvalidOperationException(LockErrorUnlessForced);
        Locked.Add((paths.ToList(), message, force));
        return Task.CompletedTask;
    }

    public Task UnlockAsync(IEnumerable<string> paths, CancellationToken ct) { Unlocked.Add(paths.ToList()); return Task.CompletedTask; }

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

    public string InfoRelativeUrl { get; set; } = "^/trunk";
    public string RepoRoot { get; set; } = "file:///repo";
    public Version? SvnVersion { get; set; } = new(1, 14, 2);
    public long InfoRevision { get; set; } = 7;
    /// <summary>Ultima revisione del ramo sul server per <see cref="GetHeadRevisionAsync"/>; <see cref="HeadError"/> la fa fallire.</summary>
    public long HeadRevision { get; set; } = 7;
    public string? HeadError { get; set; }
    public List<string> HeadUrls { get; } = [];

    public Task<long> GetHeadRevisionAsync(string url, CancellationToken ct)
    {
        HeadUrls.Add(url);
        return HeadError is not null ? throw new InvalidOperationException(HeadError) : Task.FromResult(HeadRevision);
    }

    public Task<SvnInfo> GetInfoAsync(string workingCopyPath, CancellationToken ct)
    {
        BranchCalls++;
        return Task.FromResult(new SvnInfo(RepoRoot + InfoRelativeUrl[1..], InfoRelativeUrl, RepoRoot, InfoRevision, 7, "u", null));
    }

    public Task<Version?> GetSvnVersionAsync(CancellationToken ct) => Task.FromResult(SvnVersion);

    /// <summary>Cartelle per URL (come le restituirebbe svn list); un URL assente = elenco vuoto. <see cref="Entries"/> ha la precedenza (con revisione, autore, data).</summary>
    public Dictionary<string, string[]> Folders { get; } = [];
    public Dictionary<string, SvnListEntry[]> Entries { get; } = [];
    public List<string> Listed { get; } = [];

    public Task<IReadOnlyList<SvnListEntry>> ListEntriesAsync(string url, CancellationToken ct)
    {
        Listed.Add(url);
        if (Entries.TryGetValue(url, out var rich))
            return Task.FromResult<IReadOnlyList<SvnListEntry>>(rich);
        return Task.FromResult<IReadOnlyList<SvnListEntry>>((Folders.GetValueOrDefault(url) ?? []).Select(n => new SvnListEntry(n, 0, string.Empty, null)).ToList());
    }

    /// <summary>Dati dei conflitti per percorso completo (come svn info --xml); un percorso assente = nessun dato.</summary>
    public Dictionary<string, SvnConflictInfo> ConflictInfos { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyDictionary<string, SvnConflictInfo>> GetConflictInfoAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, SvnConflictInfo>>(paths.Where(ConflictInfos.ContainsKey).ToDictionary(p => p, p => ConflictInfos[p], StringComparer.OrdinalIgnoreCase));

    public List<(string Path, string Url)> Switched { get; } = [];
    public string? SwitchError { get; set; }
    /// <summary>Quante volte lo switch fallisce con <see cref="SwitchError"/> prima di riuscire (<c>null</c> = sempre).</summary>
    public int? SwitchFailTimes { get; set; }
    /// <summary>URL relativo che <c>svn info</c> mostra dopo uno switch fallito (la radice gia' spostata: switch a meta').</summary>
    public string? InfoAfterFailedSwitch { get; set; }
    /// <summary>Ordine delle chiamate cleanup/switch (per verificare "cleanup prima di riprovare").</summary>
    public List<string> SwitchLog { get; } = [];

    public Task<UpdateResult> SwitchAsync(string workingCopyPath, string url, CancellationToken ct, long? revision = null)
    {
        SwitchLog.Add("switch " + url + (revision is { } rv ? "@" + rv : ""));
        if (SwitchError is not null && (SwitchFailTimes is null || SwitchFailTimes-- > 0))
        {
            if (InfoAfterFailedSwitch is not null)
                InfoRelativeUrl = InfoAfterFailedSwitch;
            throw new InvalidOperationException(SwitchError);
        }
        Switched.Add((workingCopyPath, url));
        return Task.FromResult(new UpdateResult(9, []));
    }

    public IReadOnlyList<long> Eligible { get; set; } = [];
    public IReadOnlyList<SvnLogEntry> RangeLog { get; set; } = [];
    public string? MergeError { get; set; }
    public MergeOutput MergeResult { get; set; } = new([], 0, 0);
    public List<(string Path, string Url, long[]? Revs, bool DryRun)> Merges { get; } = [];
    /// <summary>Stati che un merge vero lascia nella working copy.</summary>
    public List<SvnFileStatus> OnMerge { get; } = [];
    /// <summary>Revisioni ancora da unire dopo un merge vero (null = invariate).</summary>
    public IReadOnlyList<long>? EligibleAfterMerge { get; set; }

    public Task<IReadOnlyList<long>> GetEligibleRevisionsAsync(string workingCopyPath, string sourceUrl, CancellationToken ct) => Task.FromResult(Eligible);

    public Task<IReadOnlyList<SvnLogEntry>> GetLogRangeAsync(string url, long min, long max, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SvnLogEntry>>(RangeLog.Where(e => e.Revision >= min && e.Revision <= max).OrderByDescending(e => e.Revision).ToList());

    public Task<MergeOutput> MergeAsync(string workingCopyPath, string sourceUrl, IReadOnlyList<long>? revisions, bool dryRun, CancellationToken ct)
    {
        if (MergeError is not null)
            throw new InvalidOperationException(MergeError);
        Merges.Add((workingCopyPath, sourceUrl, revisions?.ToArray(), dryRun));
        if (!dryRun)
        {
            Statuses.AddRange(OnMerge);
            Eligible = EligibleAfterMerge ?? Eligible;
        }

        return Task.FromResult(MergeResult);
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

    /// <summary>Testo restituito da <see cref="AskTextAsync"/> (null = annullato).</summary>
    public string? TextResult { get; set; } = "";
    public List<string> TextPrompts { get; } = [];

    /// <summary>Risposta di <see cref="AskAsync"/> e domande ricevute (Changes non fa piu' domande sui conflitti: i test verificano che resti vuoto).</summary>
    public bool AskResult { get; set; }
    public List<string> Asked { get; } = [];

    public Task<bool> AskAsync(string message, string yes, string no, CancellationToken ct)
    {
        Asked.Add(message);
        return Task.FromResult(AskResult);
    }

    public Task<string?> AskTextAsync(string message, string label, string defaultValue, CancellationToken ct)
    {
        TextPrompts.Add(message);
        return Task.FromResult(TextResult);
    }
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
