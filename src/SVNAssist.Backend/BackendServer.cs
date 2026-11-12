using System.Text.Json;
using System.Text.Json.Serialization;
using SVNAssist.Hosting;
using SVNAssist.Rpc;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist;

/// <summary>Servizi concreti del backend (iniettabili nei test: svn, AI e prerequisiti finti).</summary>
internal sealed class BackendServices
{
    public required ISvnAssistSettings Settings { get; init; }
    public required ISvnService Svn { get; init; }
    public required IAiServiceFactory Ai { get; init; }
    public required IAiSetupPrompt AiSetupPrompt { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required PrerequisiteProbes Probes { get; init; }
    public required PrerequisiteActions PrerequisiteActions { get; init; }

    /// <summary>FileSystemWatcher sulla working copy (spento nei test).</summary>
    public bool EnableWatcher { get; init; }

    /// <summary>Servizi reali, collegati all'host per dialoghi, terminale e browser.</summary>
    public static BackendServices CreateDefault(IHost host)
    {
        var settings = new HostSettings(host);
        var processRunner = new ProcessRunner();
        var tokenResolver = new GitHubTokenResolver(processRunner);
        return new BackendServices
        {
            Settings = settings,
            // Se svn.exe manca all'avvio, ReloadableSvnService lo ritrova appena viene installato.
            Svn = new ReloadableSvnService(credentials: new HostCredentialPrompt(host)),
            Ai = new AiServiceFactory(settings, tokenResolver, processRunner),
            AiSetupPrompt = new HostAiSetupPrompt(host, settings),
            Dialogs = new HostDialogs(host),
            Probes = PrerequisiteProbes.CreateDefault(processRunner, tokenResolver),
            PrerequisiteActions = new PrerequisiteActions
            {
                LaunchConsole = async (command, claude) =>
                {
                    try
                    {
                        await host.RunInTerminalAsync(ConsoleLauncher.ShellCommand(command, claude), Environment.CurrentDirectory);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        BackendLog.Warn("RunInTerminalAsync fallita: " + ex.Message);
                        return false;
                    }
                },
                OpenUrl = host.OpenUrlAsync,
            },
            EnableWatcher = true,
        };
    }
}

/// <summary>Vista "diff" (documento di diff affiancato): il diff si apre nell'editor di VS, quindi lo stato e' sempre vuoto.</summary>
internal sealed class DiffStore : ViewStore
{
    internal override object GetSnapshot() => new DiffState("Diff", string.Empty, string.Empty);

    internal override Task<object?> DispatchAsync(string name, JsonElement args, CancellationToken ct)
        => throw new ArgumentException(Loc.T("Comando sconosciuto: {0}", name));
}

/// <summary>
/// Server JSON-RPC del backend: implementa <see cref="IBackend"/>, instrada i messaggi della UI (<c>ready</c>/<c>cmd</c>)
/// verso gli store e rimanda all'host gli snapshot (<c>state</c>, con coalescenza) e le risposte (<c>reply</c>).
/// </summary>
internal sealed class BackendServer : IBackend, IDisposable
{
    /// <summary>Coalescenza degli snapshot (protocollo: ~16 ms).</summary>
    internal TimeSpan CoalesceDelay { get; set; } = TimeSpan.FromMilliseconds(16);

    private readonly IHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Channel> _channels;

    internal ChangesStore Changes { get; }
    internal LogStore Log { get; }
    private readonly HostSettings? _hostSettings;
    private readonly ISvnAssistSettings _settings;
    private readonly PathCommands _pathCommands;

    /// <summary>Una vista collegata a uno store, con il suo contatore di revisione e l'ultimo snapshot inviato.</summary>
    private sealed class Channel
    {
        public required string View { get; init; }
        public required ViewStore Store { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Ready;
        public int Pending;
        public long Rev;
        public string? Last;
        public string? Id;
    }

    public BackendServer(IHost host, BackendServices services)
    {
        _host = host;
        _hostSettings = services.Settings as HostSettings;
        _settings = services.Settings;
        var prereq = new PrereqStore(new PrerequisitesService(services.Settings, services.Probes), services.PrerequisiteActions);
        Changes = new ChangesStore(services.Settings, services.Svn, services.Ai, services.AiSetupPrompt, services.Dialogs, host, prereq)
        {
            EnableWatcher = services.EnableWatcher,
            Recent = services.EnableWatcher ? new RecentMessages() : null, // come il watcher: spento nei test, che non devono scrivere in %LOCALAPPDATA%
        };
        Changes.CommitMessage.OpenAiWizard = ct => Changes.Wizard.OpenAsync(fromGenerate: true, ct);
        Log = new LogStore(services.Svn, services.Settings, host, Changes.TempRoot, Changes);
        Changes.StartMerge = Log.StartMergeAsync;
        Changes.MergeUndone = ct => Log.Merge?.RefreshAsync(ct) ?? Task.CompletedTask;
        _pathCommands = new PathCommands(services.Svn, host, () => Changes.RefreshAsync(_lifetime.Token), Changes.TempRoot,
            (targets, ct) => Changes.OpenRevisionPickerAsync(targets, ct));
        _channels = new Dictionary<string, Channel>
        {
            ["changes"] = new Channel { View = "changes", Store = Changes },
            ["log"] = new Channel { View = "log", Store = Log },
            ["diff"] = new Channel { View = "diff", Store = new DiffStore() },
        };

        foreach (var channel in _channels.Values)
        {
            var c = channel;
            c.Store.Changed += () => ScheduleFlush(c);
        }

        Changes.CleanTemp();
    }

    // ── IBackend ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task DispatchAsync(string view, string messageJson)
    {
        try
        {
            if (!_channels.TryGetValue(view ?? string.Empty, out var channel))
            {
                BackendLog.Warn($"Vista sconosciuta: {view}");
                return;
            }

            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                BackendLog.Warn("Messaggio UI non valido (v/type).");
                return;
            }

            switch (type.GetString())
            {
                case "ready":
                    if (root.TryGetProperty("id", out var readyId) && readyId.ValueKind == JsonValueKind.String)
                        channel.Id = readyId.GetString();
                    channel.Ready = true;
                    channel.Last = null; // pagina nuova (anche ricaricata): lo snapshot va inviato comunque
                    await ApplyLanguageAsync();
                    await channel.Store.OnReadyAsync(_lifetime.Token);
                    await FlushAsync(channel);
                    break;
                case "cmd":
                    await HandleCommandAsync(channel, root);
                    break;
                default:
                    BackendLog.Warn($"Tipo di messaggio sconosciuto: {type.GetString()}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            BackendLog.Warn("Messaggio UI non JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            BackendLog.Error("DispatchAsync", ex);
        }
    }

    private async Task HandleCommandAsync(Channel channel, JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt64(out var id)
            || !root.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            BackendLog.Warn("Comando UI non valido (id/name).");
            return;
        }

        var name = nameProp.GetString()!;
        var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        string? error = null;
        object? result = null;
        try
        {
            result = await channel.Store.DispatchAsync(name, args, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            error = Loc.T("Operazione annullata.");
        }
        catch (ArgumentException ex)
        {
            // Argomenti o chiavi non validi: errore "normale", non un guasto del backend.
            BackendLog.Info($"cmd {name} rifiutato: {ex.Message}");
            error = ex.Message;
        }
        catch (Exception ex)
        {
            BackendLog.Error($"cmd {name}", ex);
            error = ex.Message;
        }

        // Prima lo stato (con gli effetti del comando), poi la reply.
        await FlushAsync(channel);
        await SendAsync(channel.View, UiJson.Serialize(new ReplyDto(1, "reply", id, error is null, result, error)));
    }

    /// <inheritdoc />
    public async Task SettingsChangedAsync()
    {
        try
        {
            _hostSettings?.Invalidate();
            var languageChanged = await ApplyLanguageAsync();
            await Changes.OnSettingsChangedAsync(_lifetime.Token);
            // Cambio lingua a caldo: tutte le finestre aperte ricevono subito lo snapshot nella nuova lingua.
            if (languageChanged)
                foreach (var channel in _channels.Values)
                    await FlushAsync(channel);
        }
        catch (Exception ex)
        {
            BackendLog.Error("SettingsChangedAsync", ex);
        }
    }

    /// <summary>Allinea <see cref="Loc.Lang"/> all'impostazione "uiLanguage" (risolta dall'host). <c>true</c> se e' cambiata.</summary>
    private async Task<bool> ApplyLanguageAsync()
    {
        try
        {
            var lang = await _settings.GetUiLanguageAsync(_lifetime.Token);
            if (lang == Loc.Lang)
                return false;
            Loc.Lang = lang;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BackendLog.Warn("Lingua dell'interfaccia non letta: " + ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task SolutionChangedAsync(string? solutionDir)
    {
        try
        {
            await Task.WhenAll(
                Changes.OnSolutionChangedAsync(solutionDir, _lifetime.Token),
                Log.OnSolutionChangedAsync(solutionDir, _lifetime.Token));
        }
        catch (Exception ex)
        {
            BackendLog.Error("SolutionChangedAsync", ex);
        }
    }

    /// <inheritdoc />
    public Task ThemeChangedAsync(string kind)
    {
        Changes.SetVsThemeKind(kind);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RunPathCommandAsync(string command, string[] paths)
    {
        try
        {
            await _pathCommands.RunAsync(command, paths, _lifetime.Token);
        }
        catch (Exception ex)
        {
            BackendLog.Error("RunPathCommandAsync", ex);
        }
    }

    // ── Invio all'host ───────────────────────────────────────────────

    private sealed record ReplyDto(
        int V,
        string Type,
        long Id,
        bool Ok,
        object? Result,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);

    private void ScheduleFlush(Channel channel)
    {
        if (!channel.Ready || _lifetime.IsCancellationRequested || Interlocked.Exchange(ref channel.Pending, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CoalesceDelay, _lifetime.Token);
                Volatile.Write(ref channel.Pending, 0);
                await FlushAsync(channel);
            }
            catch (OperationCanceledException)
            {
                // chiusura
            }
            catch (Exception ex)
            {
                BackendLog.Error("Invio snapshot", ex);
            }
        });
    }

    /// <summary>Calcola lo snapshot e lo invia se e' cambiato rispetto all'ultimo inviato.</summary>
    private async Task FlushAsync(Channel channel)
    {
        if (!channel.Ready)
            return;

        await channel.Gate.WaitAsync();
        try
        {
            var lang = Loc.Lang;
            var stateJson = UiJson.Serialize(channel.Store.GetSnapshot());
            var signature = lang + stateJson; // un cambio di lingua rimanda lo stato anche se il resto e' uguale
            if (signature == channel.Last)
                return;

            channel.Last = signature;
            var rev = ++channel.Rev;
            var id = channel.Id is null ? string.Empty : $"\"id\":{JsonSerializer.Serialize(channel.Id)},";
            await SendAsync(channel.View, $"{{\"v\":1,\"type\":\"state\",\"view\":\"{channel.View}\",{id}\"lang\":\"{lang}\",\"rev\":{rev},\"state\":{stateJson}}}");
        }
        finally
        {
            channel.Gate.Release();
        }
    }

    /// <summary>Invia subito lo snapshot della vista (per i test).</summary>
    internal Task FlushAsync(string view) => FlushAsync(_channels[view]);

    private async Task SendAsync(string view, string json)
    {
        try
        {
            await _host.PushAsync(view, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Host chiuso o vista distrutta: nessuno a cui consegnare.
            BackendLog.Warn($"PushAsync({view}) fallita: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        Log.CleanTemp();
        _pathCommands.CleanTemp();
        Changes.Dispose();
        _lifetime.Dispose();
    }
}
