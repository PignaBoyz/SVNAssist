using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>SVN Switch (0.3.0 fase 1): parser di <c>svn info</c>/<c>svn list</c>, layout del repository, argomenti di switch, selettore e flusso.</summary>
public class SwitchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string FakeSvnPath = @"C:\fake\svn.exe";

    // ── Parser ───────────────────────────────────────────────────────

    private const string InfoXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <info><entry path="." revision="42" kind="dir">
        <url>https://svn.example.com/repo/progetto/branches/feature%20x</url>
        <relative-url>^/progetto/branches/feature%20x</relative-url>
        <repository><root>https://svn.example.com/repo</root><uuid>abc</uuid></repository>
        <commit revision="40"><author>mrossi</author><date>2026-11-10T09:30:00.123456Z</date></commit>
        </entry></info>
        """;

    [Fact]
    public void ParseInfoXml_LeggeUrlRadiceERevisioni()
    {
        var info = SvnService.ParseInfoXml(InfoXml);

        Assert.Equal("^/progetto/branches/feature%20x", info.RelativeUrl);
        Assert.Equal("https://svn.example.com/repo", info.RepositoryRoot);
        Assert.Equal(42, info.Revision);
        Assert.Equal(40, info.LastChangedRev);
        Assert.Equal("mrossi", info.LastChangedAuthor);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero).AddTicks(1234560), info.LastChangedDate);
    }

    [Fact]
    public void ParseInfoXml_SvnPreCon18_SenzaRelativeUrl_LoRicavaDaUrlERadice()
    {
        var xml = InfoXml.Replace("<relative-url>^/progetto/branches/feature%20x</relative-url>", "");

        Assert.Equal("^/progetto/branches/feature%20x", SvnService.ParseInfoXml(xml).RelativeUrl);
    }

    [Fact]
    public void ParseListXml_SoloCartelleInOrdine()
    {
        const string xml = """
            <lists><list path="https://x/repo/branches">
            <entry kind="dir"><name>zeta</name></entry>
            <entry kind="file"><name>LEGGIMI.txt</name></entry>
            <entry kind="dir"><name>Alfa</name></entry>
            </list></lists>
            """;

        Assert.Equal(["Alfa", "zeta"], SvnService.ParseListXml(xml).Select(e => e.Name));
    }

    [Theory]
    [InlineData("svn: E170000: URL 'https://x/repo/tags' doesn't exist", true)]
    [InlineData("svn: warning: W160013: 'x' path not found / E200009: Could not list all targets because some targets don't exist", true)]
    [InlineData("svn: E170013: Unable to connect to a repository at URL", false)]
    [InlineData("svn: E215004: No more credentials", false)]
    public void IsPathNotFound(string text, bool expected) => Assert.Equal(expected, SvnService.IsPathNotFound(text));

    // ── Layout ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("^/trunk", "trunk", "", "", "trunk")]
    [InlineData("^/trunk/src/Core", "trunk", "", "", "trunk")]
    [InlineData("^/branches/feature-x", "branch", "feature-x", "", "branches/feature-x")]
    [InlineData("^/branches/feature%20x/Web", "branch", "feature x", "", "branches/feature x")]
    [InlineData("^/tags/1.0", "tag", "1.0", "", "tags/1.0")]
    [InlineData("^/", "other", "", "", "/ (radice)")]
    [InlineData("^/progetto/trunk", "trunk", "", "progetto", "trunk")]
    [InlineData("^/progetto/branches/x/src", "branch", "x", "progetto", "branches/x")]
    [InlineData("^/clienti/acme/tags/v2", "tag", "v2", "clienti/acme", "tags/v2")]
    [InlineData("^/progetto", "other", "", "", "progetto")]
    [InlineData("^/branches", "other", "", "", "branches")]   // la cartella dei branch senza nome: non e' un ramo
    public void Deduce_LayoutDefault(string relativeUrl, string kind, string name, string basePath, string label)
        => Assert.Equal(new RepoPosition(kind, name, basePath, label), RepoLayout.Parse("trunk/branches/tags").Deduce(relativeUrl));

    [Fact]
    public void Deduce_LayoutPersonalizzato()
    {
        var layout = RepoLayout.Parse("principale, rami, etichette");

        Assert.Equal(new RepoPosition("branch", "x", "p", "rami/x"), layout.Deduce("^/p/rami/x"));
        Assert.Equal("other", layout.Deduce("^/trunk").Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("solo/due")]
    [InlineData("a/b/c/d")]
    public void Parse_ValoreNonValido_RicadeSuDefault(string? setting)
        => Assert.Equal(new RepoLayout("trunk", "branches", "tags"), RepoLayout.Parse(setting));

    // ── Argomenti svn ────────────────────────────────────────────────

    private sealed class Runner : IProcessRunner
    {
        public List<(IReadOnlyList<string> Args, string? Cwd)> Calls { get; } = [];
        public Func<IReadOnlyList<string>, ProcessResult> Respond { get; set; } = _ => new ProcessResult(0, "", "");

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory,
            CancellationToken ct, string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add((arguments, workingDirectory));
            return Task.FromResult(Respond(arguments));
        }
    }

    [Fact]
    public void BuildSwitchArgs_AcceptPostpone_SenzaInterazione()
        => Assert.Equal(["switch", "--accept", "postpone", "https://x/repo/branches/a%20b"], SvnService.BuildSwitchArgs("https://x/repo/branches/a%20b"));

    [Fact]
    public async Task SwitchAsync_DallaRadiceDellaWc_ConNonInteractiveEPostpone()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(0, "U    a.cs\nC    b.cs\nUpdated to revision 12.\n", "") };

        var result = await new SvnService(FakeSvnPath, runner).SwitchAsync(@"C:\wc", "https://x/repo/branches/a", Ct);

        var (args, cwd) = Assert.Single(runner.Calls);
        Assert.Equal(["switch", "--non-interactive", "--accept", "postpone", "https://x/repo/branches/a"], args);
        Assert.Equal(@"C:\wc", cwd);
        Assert.Equal(12, result.Revision);
        Assert.Equal(["b.cs"], result.ConflictedPaths);
    }

    [Fact]
    public async Task ListEntriesAsync_PercorsoInesistente_ElencoVuotoNonErrore()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(1, "", "svn: E170000: URL 'https://x/repo/tags' doesn't exist") };

        Assert.Empty(await new SvnService(FakeSvnPath, runner).ListEntriesAsync("https://x/repo/tags", Ct));
    }

    [Fact]
    public async Task ListEntriesAsync_AltriErrori_Propagano()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(1, "", "svn: E170013: Unable to connect to a repository") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SvnService(FakeSvnPath, runner).ListEntriesAsync("https://x/repo/tags", Ct));
    }

    [Fact]
    public async Task ListEntriesAsync_UsaListXml()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(0, "<lists><list><entry kind=\"dir\"><name>a</name></entry></list></lists>", "") };

        Assert.Equal(["a"], (await new SvnService(FakeSvnPath, runner).ListEntriesAsync("https://x/repo/branches", Ct)).Select(e => e.Name));
        Assert.Equal(["list", "--non-interactive", "--xml", "https://x/repo/branches"], runner.Calls[0].Args);
    }

    [Theory]
    [InlineData("1.14.2\r\n", 1, 14)]
    [InlineData("1.7.22", 1, 7)]
    public async Task GetSvnVersionAsync_LeggeLaVersione(string output, int major, int minor)
    {
        var runner = new Runner { Respond = _ => new ProcessResult(0, output, "") };
        var svn = new SvnService(FakeSvnPath, runner);

        var version = await svn.GetSvnVersionAsync(Ct);
        Assert.Equal((major, minor), (version!.Major, version.Minor));
        await svn.GetSvnVersionAsync(Ct);
        Assert.Single(runner.Calls);   // una sola lettura, poi in cache
    }

    [Fact]
    public async Task GetSvnVersionAsync_SvnNonRisponde_Null()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(1, "", "boom") };

        Assert.Null(await new SvnService(FakeSvnPath, runner).GetSvnVersionAsync(Ct));
    }

    // ── Selettore e flusso ───────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public StubSettings Settings { get; } = new();
        public ChangesStore Store { get; }

        public string RecentFile { get; }

        public Fixture(string relativeUrl = "^/trunk", string? recentFile = null)
        {
            RecentFile = recentFile ?? Path.Combine(Path.GetTempPath(), "SVNAssist_Recent_" + Guid.NewGuid().ToString("N")[..8] + ".json");
            Svn.InfoRelativeUrl = relativeUrl;
            Svn.Folders["file:///repo"] = ["branches", "tags", "trunk"];
            Svn.Folders["file:///repo/branches"] = ["feature-x", "con spazio"];
            Svn.Folders["file:///repo/tags"] = ["1.0"];
            Store = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, new FakeHost(),
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())), recentTargetsFile: RecentFile)
            { WorkingCopyPath = @"C:\wc", SwitchRetryDelay = TimeSpan.Zero, TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Sw_" + Guid.NewGuid().ToString("N")[..8]) };
        }

        public SwitchPickerDto Picker => ((ChangesState)Store.GetSnapshot()).SwitchPicker;
        public ChangesState State => (ChangesState)Store.GetSnapshot();
        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
        public void Dispose() => Store.Dispose();
    }

    [Fact]
    public async Task Selettore_ElencaTrunkBranchETag_ConIlCorrenteMarcato()
    {
        using var f = new Fixture("^/branches/feature-x/src");

        await f.Cmd("switchOpen");

        Assert.True(f.Picker.Open);
        Assert.Equal(
            [("trunk", "trunk", false), ("branches/feature-x", "branch", true), ("branches/con spazio", "branch", false), ("tags/1.0", "tag", false)],
            f.Picker.Items.Select(i => (i.Name, i.Kind, i.Current)));
    }

    [Fact]
    public async Task Selettore_ProgettoSottoUnSottopercorso_UsaLaBaseDedotta()
    {
        using var f = new Fixture("^/progetto/trunk");
        f.Svn.Folders["file:///repo/progetto"] = ["trunk", "branches"];
        f.Svn.Folders["file:///repo/progetto/branches"] = ["b1"];

        await f.Cmd("switchOpen");

        Assert.Equal(["trunk", "branches/b1"], f.Picker.Items.Select(i => i.Name));
        Assert.Contains("file:///repo/progetto/tags", f.Svn.Listed);   // tags non esiste: elenco vuoto, non errore
        Assert.Equal("", f.Picker.Error);
    }

    [Fact]
    public async Task Selettore_CacheDiSessione_AggiornaRileggeDalServer()
    {
        using var f = new Fixture();
        await f.Cmd("switchOpen");
        var first = f.Svn.Listed.Count;
        await f.Cmd("switchCancel");

        await f.Cmd("switchOpen");
        Assert.Equal(first, f.Svn.Listed.Count);

        f.Svn.Folders["file:///repo/tags"] = ["1.0", "2.0"];
        await f.Cmd("switchRefresh");
        Assert.Equal(first * 2, f.Svn.Listed.Count);
        Assert.Contains("tags/2.0", f.Picker.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Switch_OK_UsaLUrlDellElencoCodificato_RefreshEAvvisoSolution()
    {
        using var f = new Fixture();
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "branches/con spazio" });

        Assert.Equal([(@"C:\wc", "file:///repo/branches/con%20spazio")], f.Svn.Switched);
        Assert.False(f.Picker.Open);
        var status = ((ChangesState)f.Store.GetSnapshot()).Status.Msg;
        Assert.Contains("branches/con spazio", status);
        Assert.Contains("ricarica la solution", status);
        Assert.True(f.Svn.StatusCalls > 0);
    }

    [Fact]
    public async Task Switch_NomeNonInElenco_Rifiutato_NienteSwitch()
    {
        using var f = new Fixture();
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "branches/../../altro" });

        Assert.Empty(f.Svn.Switched);
        Assert.True(f.Picker.Open);
        Assert.NotEqual("", f.Picker.Error);
    }

    [Fact]
    public async Task Switch_SulRamoCorrente_Rifiutato()
    {
        using var f = new Fixture();
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "trunk" });

        Assert.Empty(f.Svn.Switched);
        Assert.Contains("trunk", f.Picker.Error);
    }

    [Fact]
    public async Task Switch_ConModificheLocali_ChiedeConferma()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\a.cs", SvnStatusKind.Modified, IsTreeConflict: false));
        await f.Store.RefreshAsync(Ct);
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });
        Assert.Empty(f.Svn.Switched);   // avviso con il riepilogo, niente switch
        var warn = ((ChangesState)f.Store.GetSnapshot()).SwitchWarn!;
        Assert.Equal(1, warn.Counts.Modified);
        Assert.Equal(1, warn.Total);
        await f.Cmd("switchWarnCancel");
        Assert.Null(((ChangesState)f.Store.GetSnapshot()).SwitchWarn);
        Assert.Empty(f.Svn.Switched);

        await f.Cmd("switchOpen");
        await f.Cmd("switchOk", new { name = "tags/1.0" });
        await f.Cmd("switchWarnCleanup");   // Apri Cleanup: dopo, torna l'avviso
        Assert.NotNull(((ChangesState)f.Store.GetSnapshot()).Cleanup);
        await f.Cmd("cleanupCancel");
        Assert.NotNull(((ChangesState)f.Store.GetSnapshot()).SwitchWarn);

        await f.Cmd("switchWarnContinue");
        Assert.Single(f.Svn.Switched);
        Assert.False(((ChangesState)f.Store.GetSnapshot()).Progress!.Running);
    }

    [Fact]
    public async Task Switch_SoloNonVersionati_AvvisaConIlConteggio()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\nuovo.txt", SvnStatusKind.Unversioned, IsTreeConflict: false));
        await f.Store.RefreshAsync(Ct);
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });

        Assert.Empty(f.Svn.Switched);
        Assert.Equal(1, ((ChangesState)f.Store.GetSnapshot()).SwitchWarn!.Counts.Unversioned);
    }

    [Fact]
    public async Task Switch_ErroreDiSvn_FinisceNelloStato()
    {
        using var f = new Fixture();
        f.Svn.SwitchError = "boom";
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });

        Assert.Contains("boom", ((ChangesState)f.Store.GetSnapshot()).Status.Msg);
    }

    [Fact]
    public async Task Refresh_RamoDedottoDalLayoutEVersioneSvnNelloSnapshot()
    {
        using var f = new Fixture("^/progetto/branches/x/src");

        await f.Store.RefreshAsync(Ct);

        var wc = ((ChangesState)f.Store.GetSnapshot()).WorkingCopy;
        Assert.Equal("branches/x", wc.Branch);
        Assert.Equal("1.14.2", wc.SvnVersion);
    }

    // ── 0.3.2: switch interrotto a meta' (E155009, cartella tenuta aperta da VS) ──

    private const string WorkQueueError = "svn switch failed (exit code 1): svn: E155009: Failed to run the WC DB work queue associated with 'C:\\wc\\managed', work item 19759 (dir-remove managed/Views/CommandBar)";

    [Fact]
    public void SvnErrors_E155009_ChiedeCleanup()
    {
        Assert.True(SvnErrors.IsWorkingCopyLocked(WorkQueueError));
        Assert.True(SvnErrors.IsWorkingCopyLocked("svn: Failed to run the WC DB work queue associated with 'x'"));
    }

    [Fact]
    public async Task Switch_ErroreDaCleanup_RiprovaDaSoloDopoIlCleanup()
    {
        using var f = new Fixture();
        f.Svn.SwitchError = WorkQueueError;
        f.Svn.SwitchFailTimes = 1;
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });

        Assert.Equal(["switch file:///repo/tags/1.0", "cleanup", "switch file:///repo/tags/1.0"], f.Svn.SwitchLog);
        Assert.False(f.State.Status.Err);
        Assert.Contains("tags/1.0", f.State.Status.Msg);
        Assert.Null(f.State.Status.Action);
        Assert.DoesNotContain("incompleto", f.State.WorkingCopy.Branch);
    }

    [Fact]
    public async Task Switch_InterrottoAMeta_ErroreCompletoConCompletaSwitch_PoiCompleta()
    {
        using var f = new Fixture();
        f.Svn.SwitchError = WorkQueueError;
        f.Svn.InfoAfterFailedSwitch = "^/tags/1.0";   // svn ha gia' spostato la radice
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });

        var st = f.State;
        Assert.True(st.Status.Err);
        Assert.Contains("work item 19759 (dir-remove managed/Views/CommandBar)", st.Status.Msg);   // testo intero, non troncato
        Assert.Contains("Revert", st.Status.Msg);
        Assert.Equal("switchResume", st.Status.Action?.Cmd);
        Assert.Contains("switch incompleto", st.WorkingCopy.Branch);   // intestazione riletta, non piu' il ramo vecchio

        // Il selettore non dice "(corrente)" sulla destinazione: e' incompleta e si puo' scegliere
        await f.Cmd("switchOpen");
        var item = f.Picker.Items.Single(i => i.Name == "tags/1.0");
        Assert.False(item.Current);
        Assert.True(item.Incomplete);
        await f.Cmd("switchCancel");

        f.Svn.SwitchError = null;
        f.Svn.SwitchLog.Clear();
        await f.Cmd("switchResume");

        Assert.Equal(["cleanup", "switch file:///repo/tags/1.0"], f.Svn.SwitchLog);
        Assert.False(f.State.Status.Err);
        Assert.Null(f.State.Status.Action);
        Assert.DoesNotContain("incompleto", f.State.WorkingCopy.Branch);
        await f.Cmd("switchOpen");
        Assert.True(f.Picker.Items.Single(i => i.Name == "tags/1.0").Current);
    }

    [Fact]
    public async Task Switch_Interrotto_TornaAPrimaDelloSwitch_UrlERevisioneDiPartenza()
    {
        using var f = new Fixture();
        var fromUrl = f.Svn.RepoRoot + f.Svn.InfoRelativeUrl[1..];
        var fromRev = f.Svn.InfoRevision;
        f.Svn.SwitchError = WorkQueueError;
        f.Svn.InfoAfterFailedSwitch = "^/tags/1.0";
        await f.Cmd("switchOpen");
        await f.Cmd("switchOk", new { name = "tags/1.0" });
        Assert.Equal("switchResume", f.State.Status.Action?.Cmd);
        Assert.Equal("switchBack", f.State.Status.Action2?.Cmd);

        f.Svn.SwitchError = null;
        f.Svn.SwitchLog.Clear();
        await f.Cmd("switchBack");

        Assert.Equal(["cleanup", $"switch {fromUrl}@{fromRev}"], f.Svn.SwitchLog);   // solo cleanup normale, poi switch -r alla partenza
        Assert.Equal([CleanupOptions.Default], f.Svn.CleanupOptionsUsed.Distinct().ToList());
        Assert.Null(f.State.Status.Action2);
        Assert.False(f.State.Status.Err);
    }

    [Fact]
    public void PendingSwitch_VoceVecchiaSenzaPartenza_NonPuoTornareIndietro()
    {
        var file = Path.Combine(Path.GetTempPath(), "pending-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(file, "{\"C:\\\\WC\":{\"Name\":\"tags/1.0\",\"Url\":\"file:///r/tags/1.0\"}}");
        var p = new PendingSwitch(file);
        Assert.False(p.Get(@"C:\wc")!.CanGoBack);
        p.Set(@"C:\wc", new PendingSwitch.Entry("t", "u", "file:///r/trunk", 5, "trunk"));
        Assert.True(new PendingSwitch(file).Get(@"C:\wc")!.CanGoBack);
        File.Delete(file);
    }

    [Fact]
    public async Task Switch_Interrotto_SceltaDellaVoceIncompleta_CompletaSenzaConferma()
    {
        using var f = new Fixture();
        f.Svn.SwitchError = WorkQueueError;
        f.Svn.InfoAfterFailedSwitch = "^/tags/1.0";
        await f.Cmd("switchOpen");
        await f.Cmd("switchOk", new { name = "tags/1.0" });
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\managed", SvnStatusKind.Incomplete));   // cartelle "!" lasciate da svn
        await f.Store.RefreshAsync(Ct);

        f.Svn.SwitchError = null;
        f.Svn.SwitchLog.Clear();
        await f.Cmd("switchOpen");
        await f.Cmd("switchOk", new { name = "tags/1.0" });

        Assert.Empty(f.Dialogs.Messages);   // niente "hai modifiche locali": non sono lavoro dell'utente
        Assert.Equal(["cleanup", "switch file:///repo/tags/1.0"], f.Svn.SwitchLog);
        Assert.Equal("", f.Picker.Error);
    }

    [Fact]
    public async Task Switch_ErroreSenzaToccareLaWc_ErroreNormale_NienteDaCompletare()
    {
        using var f = new Fixture();
        f.Svn.SwitchError = "svn: E170001: Authorization failed";
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });

        Assert.Equal(["switch file:///repo/tags/1.0"], f.Svn.SwitchLog);   // nessun nuovo tentativo: non e' un errore da cleanup
        Assert.Contains("E170001", f.State.Status.Msg);
        Assert.NotEqual("switchResume", f.State.Status.Action?.Cmd);
        Assert.DoesNotContain("incompleto", f.State.WorkingCopy.Branch);
        await f.Cmd("switchOpen");
        Assert.All(f.Picker.Items, i => Assert.Null(i.Incomplete));
    }

    [Fact]
    public void Avanzamento_Annulla_SoloSwitchUpdateMerge_UnaVolta()
    {
        var p = new OperationProgress();
        var ct = p.Begin("cleanup", "c", "f");
        Assert.False(p.GetSnapshot()!.Cancellable);
        Assert.False(p.Cancel());
        Assert.False(ct.IsCancellationRequested);

        ct = p.Begin("merge", "m", "f");
        Assert.True(p.GetSnapshot()!.Cancellable);
        Assert.True(p.Cancel());
        Assert.False(p.Cancel());   // doppio click
        Assert.True(ct.IsCancellationRequested);
        Assert.True(p.GetSnapshot()!.Cancelling);
    }

    [Fact]
    public async Task ProcessRunner_TokenAnnullato_TerminaIlProcesso()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var run = new ProcessRunner().RunStreamingAsync("ping.exe", ["-n", "60", "127.0.0.1"], null, cts.Token, null, null, _ => started.TrySetResult());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "ping avrebbe girato per 60 secondi: il processo non e' stato ucciso");
    }

    [Fact]
    public async Task Switch_Annullato_LasciaPendingSwitch_CleanupEAzioniNellaFinestra()
    {
        using var f = new Fixture();
        f.Svn.Hang = true;
        await f.Cmd("switchOpen");
        var run = f.Cmd("switchOk", new { name = "tags/1.0" });
        for (var i = 0; i < 200 && !f.Svn.SwitchLog.Contains("switch file:///repo/tags/1.0"); i++)
            await Task.Delay(10);
        Assert.True(f.State.Progress!.Cancellable);

        await f.Cmd("opCancel");
        await run;

        var p = f.State.Progress!;
        Assert.True(p.Cancelled);
        Assert.False(p.Running);
        Assert.Equal(["switchResume", "switchBack", "cleanup"], p.Actions!.Select(a => a.Cmd));
        Assert.Equal(["switch file:///repo/tags/1.0", "cleanup"], f.Svn.SwitchLog);   // cleanup normale dopo l'annullamento
        Assert.Equal("switchResume", f.State.Status.Action?.Cmd);   // PendingSwitch resta: flusso esistente
        Assert.Empty(f.Svn.Switched);

        await f.Cmd("cleanup");   // "Cleanup…" dalla finestra: chiude l'avanzamento e apre le opzioni
        Assert.Null(f.State.Progress);
        Assert.NotNull(f.State.Cleanup);
    }

    [Fact]
    public async Task Update_Annullato_ProponeRiprovaECleanup()
    {
        using var f = new Fixture();
        f.Svn.Hang = true;
        var run = f.Cmd("update");
        for (var i = 0; i < 200 && f.Svn.Updates == 0; i++)
            await Task.Delay(10);

        await f.Cmd("opCancel");
        await run;

        Assert.Equal(["update", "cleanup"], f.State.Progress!.Actions!.Select(a => a.Cmd));
        Assert.True(f.State.Progress.Cancelled);
        Assert.Equal(["cleanup"], f.Svn.SwitchLog);
    }

    [Fact]
    public async Task Switch_Interrotto_SiRicordaAncheDopoIlRiavvio()
    {
        string recent;
        using (var f = new Fixture())
        {
            recent = f.RecentFile;
            f.Svn.SwitchError = WorkQueueError;
            f.Svn.InfoAfterFailedSwitch = "^/tags/1.0";
            await f.Cmd("switchOpen");
            await f.Cmd("switchOk", new { name = "tags/1.0" });
        }

        using var g = new Fixture("^/tags/1.0", recent);   // nuova sessione di VS, stessa working copy
        await g.Store.RefreshAsync(Ct, reloadBranch: true);

        Assert.True(g.State.Status.Err);
        Assert.Contains("tags/1.0", g.State.Status.Msg);
        Assert.Equal("switchResume", g.State.Status.Action?.Cmd);
    }
}
