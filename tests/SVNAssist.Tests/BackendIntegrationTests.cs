using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Backend completo (<see cref="BackendServer"/>) con un host finto e un repository svn locale (file://):
/// ready -> state, stage + commit, eliminazione file. Richiede svn.exe e svnadmin.exe (come SvnServiceTests).
/// Ogni messaggio spedito alla UI viene validato contro gli schemi del contratto.
/// </summary>
public sealed class BackendIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SVNAssist_Int_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _svn = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
    private readonly string _svnadmin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
    private readonly string _wc;
    private readonly FakeHost _host = new();
    private BackendServer? _server;
    private int _cmdId;

    public BackendIntegrationTests()
    {
        var repo = Path.Combine(_root, "repo");
        _wc = Path.Combine(_root, "wc");
        Directory.CreateDirectory(_root);
        Run(_svnadmin, $"create \"{repo}\"");
        Run(_svn, $"checkout \"file:///{repo.Replace('\\', '/')}\" \"{_wc}\"");
    }

    public void Dispose()
    {
        _server?.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private string Run(string exe, string args, string? cwd = null)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd ?? _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{Path.GetFileName(exe)} {args}: {error}");
        return output;
    }

    private string Svn(string args) => Run(_svn, args, _wc);

    private void Commit(string relative, string content, string message)
    {
        File.WriteAllText(Path.Combine(_wc, relative), content);
        Svn($"add \"{relative}\"");
        Svn($"commit -m \"{message}\"");
        Svn("update"); // la radice della working copy resta a r0 finche' non si aggiorna: senza, svn log non vede nulla
    }

    private BackendServer StartServer()
    {
        _host.Settings["workingCopyPath"] = _wc;
        var services = new BackendServices
        {
            Settings = new HostSettings(_host),
            Svn = new SvnService(_svn),
            Ai = new FakeAiFactory(),
            AiSetupPrompt = new NoopAiPrompt(),
            Dialogs = new Hosting.HostDialogs(_host),
            Probes = new PrerequisiteProbes { FindSvn = () => _svn },
            PrerequisiteActions = new Stores.PrerequisiteActions(),
            EnableWatcher = false,
        };
        _server = new BackendServer(_host, services) { CoalesceDelay = TimeSpan.FromMilliseconds(1) };
        return _server;
    }

    private static string Msg(object message) => JsonSerializer.Serialize(message);

    private async Task SendAsync(string view, object message) => await _server!.DispatchAsync(view, Msg(message));

    private async Task OpenAsync(string view = "changes") => await SendAsync(view, new { v = 1, type = "ready", view, id = (string?)null });

    /// <summary>Esegue un comando e ne ritorna la reply (lo stato aggiornato e' gia' stato spedito prima della reply).</summary>
    private async Task<JsonNode> CmdAsync(string name, object? args = null, string view = "changes")
    {
        var id = ++_cmdId;
        await SendAsync(view, new { v = 1, id, type = "cmd", name, args = args ?? new { } });
        return Pushed(view).Last(m => m["type"]!.GetValue<string>() == "reply" && m["id"]!.GetValue<int>() == id);
    }

    private List<JsonNode> Pushed(string view = "changes")
    {
        lock (_host.Pushed)
            return _host.Pushed.Where(p => p.View == view).Select(p => JsonNode.Parse(p.Json)!).ToList();
    }

    private JsonNode LastState(string view = "changes") => Pushed(view).Last(m => m["type"]!.GetValue<string>() == "state")["state"]!;

    private async Task<JsonNode> WaitStateAsync(Func<JsonNode, bool> condition, string view = "changes")
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var states = Pushed(view).Where(m => m["type"]!.GetValue<string>() == "state").ToList();
            if (states.Count > 0 && condition(states[^1]["state"]!))
                return states[^1]["state"]!;
            await Task.Delay(50);
        }

        throw new TimeoutException("Stato atteso non raggiunto. Ultimo: " + (Pushed(view).LastOrDefault()?.ToJsonString() ?? "(nessun messaggio)"));
    }

    private static IEnumerable<string> Keys(JsonNode state, string section)
        => state["sections"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == section)!["rows"]!.AsArray()
            .Where(r => r!["kind"]!.GetValue<string>() == "file").Select(r => r!["key"]!.GetValue<string>());

    /// <summary>Ogni messaggio verso la UI deve rispettare envelope e schema di stato.</summary>
    private void AssertAllMessagesValid(string view = "changes")
    {
        long lastRev = 0;
        lock (_host.Pushed)
        {
            foreach (var (v, json) in _host.Pushed.Where(p => p.View == view))
            {
                Assert.Empty(SnapshotSchemaTests.Validate("envelope", json));
                var node = JsonNode.Parse(json)!;
                if (node["type"]!.GetValue<string>() != "state")
                    continue;

                Assert.Empty(SnapshotSchemaTests.Validate(view == "changes" ? "changes-state" : $"{view}-state", node["state"]!.ToJsonString()));
                var rev = node["rev"]!.GetValue<long>();
                Assert.True(rev > lastRev, "rev deve crescere");
                lastRev = rev;
            }
        }
    }

    [Fact]
    public async Task Ready_RilevaLaWorkingCopy_EInviaSnapshotConformi()
    {
        Commit("a.txt", "uno\n", "init");
        File.WriteAllText(Path.Combine(_wc, "a.txt"), "uno\ndue\n");
        File.WriteAllText(Path.Combine(_wc, "n.txt"), "nuovo\n");
        StartServer();

        await OpenAsync();
        var first = Pushed().First();
        Assert.Equal("state", first["type"]!.GetValue<string>());
        Assert.Equal(1, first["rev"]!.GetValue<int>());

        var state = await WaitStateAsync(s => !s["loading"]!.GetValue<bool>() && s["workingCopy"]!["branch"]!.GetValue<string>() != "");

        Assert.Equal(_wc, state["workingCopy"]!["path"]!.GetValue<string>());
        Assert.Empty(Keys(state, "staged")); // come Git: nulla in Staged da solo
        Assert.Equal(["changes:a.txt"], Keys(state, "changes"));
        Assert.Equal(["unversioned:n.txt"], Keys(state, "unversioned"));
        Assert.True(state["commit"]!["all"]!.GetValue<bool>()); // Commit All
        Assert.Equal(1, state["commit"]!["count"]!.GetValue<int>());
        AssertAllMessagesValid();
    }

    [Fact]
    public async Task StageECommit_CommittaSoloCioCheEInStaged()
    {
        Commit("a.txt", "uno\n", "init");
        Commit("b.txt", "uno\n", "init b");
        File.WriteAllText(Path.Combine(_wc, "a.txt"), "uno\ndue\n");
        File.WriteAllText(Path.Combine(_wc, "b.txt"), "uno\ndue\n");
        File.WriteAllText(Path.Combine(_wc, "n.txt"), "nuovo\n");
        File.WriteAllText(Path.Combine(_wc, "x.txt"), "non voglio\n");
        StartServer();
        await OpenAsync();
        await WaitStateAsync(s => Keys(s, "changes").Count() == 2);

        Assert.True((await CmdAsync("stage", new { key = "changes:a.txt" }))["ok"]!.GetValue<bool>());
        Assert.True((await CmdAsync("stage", new { key = "unversioned:n.txt" }))["ok"]!.GetValue<bool>());
        Assert.True((await CmdAsync("setCommitText", new { text = "Commit dal test d'integrazione" }))["ok"]!.GetValue<bool>());
        var state = LastState();
        Assert.Equal(["staged:a.txt", "staged:n.txt"], Keys(state, "staged"));

        var reply = await CmdAsync("commit");

        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        var after = LastState();
        Assert.Contains("revisione 3", after["status"]!["msg"]!.GetValue<string>());
        Assert.Empty(Keys(after, "staged"));
        Assert.Equal("", after["commit"]!["text"]!.GetValue<string>());
        Assert.Equal(["changes:b.txt"], Keys(after, "changes"));
        Assert.Equal(["unversioned:x.txt"], Keys(after, "unversioned"));

        var log = Svn("log -v -r HEAD");
        Assert.Contains("Commit dal test d'integrazione", log);
        Assert.Contains("/a.txt", log);
        Assert.Contains("/n.txt", log);
        Assert.DoesNotContain("/b.txt", log);
        Assert.DoesNotContain("/x.txt", log);
        AssertAllMessagesValid();
    }

    [Fact]
    public async Task DeleteFile_ConfermaDallHost_VersionatoEdNonVersionato()
    {
        Commit("v.txt", "uno\n", "init");
        File.WriteAllText(Path.Combine(_wc, "v.txt"), "uno\nmodificato\n");
        File.WriteAllText(Path.Combine(_wc, "u.txt"), "non versionato\n");
        StartServer();
        _server!.Changes.DeleteFromDisk = File.Delete; // niente cestino reale nei test
        await OpenAsync();
        await WaitStateAsync(s => Keys(s, "changes").Any() && Keys(s, "unversioned").Any());

        // Annullato dall'utente (secondo pulsante): non cambia nulla.
        _host.PromptAnswers.Enqueue(1);
        Assert.True((await CmdAsync("rowOp", new { op = "deleteFile", key = "unversioned:u.txt" }))["ok"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(_wc, "u.txt")));

        // Confermato: il non versionato sparisce dal disco, il versionato diventa D (svn delete --force).
        Assert.True((await CmdAsync("rowOp", new { op = "deleteFile", key = "unversioned:u.txt" }))["ok"]!.GetValue<bool>());
        Assert.True((await CmdAsync("rowOp", new { op = "deleteFile", key = "changes:v.txt" }))["ok"]!.GetValue<bool>());

        Assert.False(File.Exists(Path.Combine(_wc, "u.txt")));
        Assert.False(File.Exists(Path.Combine(_wc, "v.txt")));
        Assert.Contains("D       v.txt", Svn("status"));
        var state = LastState();
        Assert.Equal(["changes:v.txt"], Keys(state, "changes"));
        Assert.Equal("D", state["sections"]![1]!["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Empty(Keys(state, "unversioned"));
        Assert.Equal(3, _host.Prompts.Count);
        AssertAllMessagesValid();
    }

    [Fact]
    public async Task ShowDiff_EsportaBaseVeraEApreIlDiffNellHost()
    {
        Commit("a.txt", "originale\n", "init");
        File.WriteAllText(Path.Combine(_wc, "a.txt"), "modificato\n");
        StartServer();
        await OpenAsync();
        await WaitStateAsync(s => Keys(s, "changes").Any());

        var reply = await CmdAsync("showDiff", new { key = "changes:a.txt" });

        Assert.True(reply["ok"]!.GetValue<bool>());
        var diff = Assert.Single(_host.Diffs);
        Assert.Equal(Path.Combine(_wc, "a.txt"), diff.Right);
        Assert.Equal("originale\n", File.ReadAllText(diff.Left).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ChiaveNonValida_RispondeOkFalse_ENonTocca_IlFileSystem()
    {
        Commit("a.txt", "uno\n", "init");
        StartServer();
        await OpenAsync();
        await WaitStateAsync(s => !s["loading"]!.GetValue<bool>());
        var outside = Path.Combine(_root, "fuori.txt");
        File.WriteAllText(outside, "intoccabile");

        var reply = await CmdAsync("rowOp", new { op = "deleteFile", key = "unversioned:../fuori.txt" });

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("aggiorna", reply["error"]!.GetValue<string>());
        Assert.True(File.Exists(outside));
        Assert.Empty(_host.Prompts);
    }

    [Fact]
    public async Task Log_Ready_CaricaLaCronologiaConIPercorsi()
    {
        Commit("a.txt", "uno\n", "primo");
        StartServer();
        await OpenAsync("log");

        var state = await WaitStateAsync(s => s["entries"]!.AsArray().Count > 0, "log");

        Assert.Equal(1, state["entries"]![0]!["rev"]!.GetValue<int>());
        Assert.Equal("/a.txt", state["entries"]![0]!["paths"]![0]!["p"]!.GetValue<string>());
        Assert.Equal("A", state["entries"]![0]!["paths"]![0]!["a"]!.GetValue<string>());
        var reply = await CmdAsync("logSelect", new { rev = 1 }, "log");
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Contains("a.txt", LastState("log")["diff"]!.GetValue<string>());
        AssertAllMessagesValid("log");
    }

    [Fact]
    public async Task MessaggiNonValidi_VengonoIgnorati_SenzaFarCadereIlServer()
    {
        StartServer();

        await _server!.DispatchAsync("changes", "{non json");
        await _server.DispatchAsync("changes", Msg(new { v = 2, type = "ready" }));
        await _server.DispatchAsync("vistaSconosciuta", Msg(new { v = 1, type = "ready" }));
        await SendAsync("changes", new { v = 1, id = 1, type = "cmd" }); // senza name

        Assert.Empty(Pushed());
        await OpenAsync(); // il server funziona ancora
        Assert.NotEmpty(Pushed());
    }
}
