using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Indicatore della revisione, controllo periodico "altri hanno committato?", messaggi di commit recenti e messaggio del merge stile TortoiseSVN.</summary>
[Collection("Loc")]
public class RevisionWatchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── Calcolo dello stato ──

    [Theory]
    [InlineData(1234, 1234, "current")]
    [InlineData(1234, 1100, "current")]   // il ramo non cambia da piu' tempo di quanto la working copy sia aggiornata
    [InlineData(1234, 1240, "behind")]
    [InlineData(1234, null, "unknown")]
    [InlineData(null, 1240, "unknown")]
    [InlineData(0, 5, "unknown")]
    public void Indicator_ConfrontaWorkingCopyEHead(int? wc, int? head, string expected)
        => Assert.Equal(expected, RevisionIndicator.Compute(wc, head));

    // ── Watcher ──

    private sealed class Probe
    {
        public int Fetches, Changes;
        public bool Busy;
        public int Minutes = 5;
        public long? Head = 10;
        public string? Error;
        public TaskCompletionSource? Gate;
        public List<TimeSpan> Waits { get; } = [];
        public CancellationTokenSource Stop { get; } = new();
        public int StopAfterWaits = int.MaxValue;

        public HeadWatcher Watcher => new(async ct =>
            {
                Fetches++;
                if (Gate is not null)
                    await Gate.Task;
                return Error is not null ? throw new InvalidOperationException(Error) : Head;
            },
            () => Busy, _ => Task.FromResult(Minutes), () => Changes++,
            (wait, ct) =>
            {
                Waits.Add(wait);
                if (Waits.Count >= StopAfterWaits)
                    Stop.Cancel();
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Watcher_Controllo_MemorizzaLaHead()
    {
        var p = new Probe();
        var w = p.Watcher;

        Assert.True(await w.CheckAsync(Ct));

        Assert.Equal(10, w.Head);
        Assert.Null(w.Error);
        Assert.Equal(1, p.Changes);
    }

    [Fact]
    public async Task Watcher_ErroreDiReteOAutenticazione_NonLancia_Neutro_ConIlPerche()
    {
        var p = new Probe { Error = "svn: E170001: Authentication failed" };
        var w = p.Watcher;
        await w.CheckAsync(Ct);

        Assert.Null(w.Head);
        Assert.Contains("E170001", w.Error);
        Assert.Equal("unknown", RevisionIndicator.Compute(7, w.Head));

        p.Error = null;   // la rete torna: l'esito si riprende da solo
        await w.CheckAsync(Ct);
        Assert.Equal(10, w.Head);
        Assert.Null(w.Error);
    }

    [Fact]
    public async Task Watcher_NonParteSeUnOperazioneDellUtenteECorso()
    {
        var p = new Probe { Busy = true };
        var w = p.Watcher;

        Assert.False(await w.CheckAsync(Ct));
        Assert.Equal(0, p.Fetches);

        p.Busy = false;
        Assert.True(await w.CheckAsync(Ct));
        Assert.Equal(1, p.Fetches);
    }

    [Fact]
    public async Task Watcher_DueControlliNonSiSovrappongono()
    {
        var p = new Probe { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var w = p.Watcher;

        var first = w.CheckAsync(Ct);
        Assert.False(await w.CheckAsync(Ct));   // il primo non e' finito
        Assert.Equal(1, p.Fetches);

        p.Gate.SetResult();
        Assert.True(await first);
        Assert.True(await w.CheckAsync(Ct));    // finito: se ne puo' fare un altro
        Assert.Equal(2, p.Fetches);
    }

    [Fact]
    public async Task Watcher_Ciclo_ControllaOgniNMinuti()
    {
        var p = new Probe { Minutes = 5, StopAfterWaits = 3 };
        var w = p.Watcher;

        await w.RunAsync(p.Stop.Token);

        Assert.Equal(3, p.Fetches);
        Assert.All(p.Waits, t => Assert.Equal(TimeSpan.FromMinutes(5), t));
    }

    [Fact]
    public async Task Watcher_Ciclo_ZeroMinutiESpento_NessunaRichiestaAlServer()
    {
        var p = new Probe { Minutes = 0, StopAfterWaits = 2 };
        var w = p.Watcher;

        await w.RunAsync(p.Stop.Token);

        Assert.Equal(0, p.Fetches);
        Assert.True(w.Disabled);
        Assert.All(p.Waits, t => Assert.Equal(HeadWatcher.PollDisabled, t));   // rilegge l'impostazione ogni tanto
    }

    [Fact]
    public async Task Watcher_VsInSecondoPiano_NonControlla_Ciclo_AspettaSenzaRichieste_AlRitornoControlla()
    {
        var p = new Probe { StopAfterWaits = 2 };
        var w = p.Watcher;
        w.SetActive(false);

        Assert.False(await w.CheckAsync(Ct));
        await w.RunAsync(p.Stop.Token);
        Assert.Equal(0, p.Fetches);
        Assert.All(p.Waits, t => Assert.Equal(HeadWatcher.PollDisabled, t));

        w.SetActive(true);
        Assert.True(await w.CheckAsync(Ct));
        Assert.Equal(1, p.Fetches);
    }

    [Fact]
    public async Task Watcher_Ciclo_OccupatoRiprovaPresto_NonFraNMinuti()
    {
        var p = new Probe { Busy = true, StopAfterWaits = 1 };
        await p.Watcher.RunAsync(p.Stop.Token);

        Assert.Equal(0, p.Fetches);
        Assert.Equal(HeadWatcher.RetryBusy, Assert.Single(p.Waits));
    }

    // ── Integrazione nello store: indicatore nello snapshot ──

    private sealed class Fixture
    {
        public FakeSvnService Svn { get; } = new() { InfoRelativeUrl = "^/trunk", InfoRevision = 1234, HeadRevision = 1234 };
        public StubSettings Settings { get; } = new();
        public ChangesStore Store { get; }
        public string RecentFile { get; } = Path.Combine(Path.GetTempPath(), "SVNAssist_RecentMsg_" + Guid.NewGuid().ToString("N")[..8] + ".json");

        public Fixture()
        {
            Store = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())),
                recentTargetsFile: Path.Combine(Path.GetTempPath(), "SVNAssist_Recent_" + Guid.NewGuid().ToString("N")[..8] + ".json"))
            {
                WorkingCopyPath = @"C:\wc",
                Recent = new RecentMessages(RecentFile),
            };
        }

        public ChangesState Snap => (ChangesState)Store.GetSnapshot();

        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
    }

    [Fact]
    public async Task Snapshot_AllaHead_Verde_ConRevisioneEHead()
    {
        var f = new Fixture();
        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        await f.Store.HeadCheck.CheckAsync(Ct);

        var wc = f.Snap.WorkingCopy;
        Assert.Equal((1234L, 1234L, "current", null), (wc.Rev, wc.Head, wc.RevState, wc.RevNote));
        Assert.Equal("file:///repo/trunk", Assert.Single(f.Svn.HeadUrls));   // l'URL della working copy, nient'altro
    }

    [Fact]
    public async Task Snapshot_ServerAvanti_Arancio()
    {
        var f = new Fixture();
        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        f.Svn.HeadRevision = 1240;
        await f.Store.HeadCheck.CheckAsync(Ct);

        var wc = f.Snap.WorkingCopy;
        Assert.Equal((1234L, 1240L, "behind"), (wc.Rev, wc.Head, wc.RevState));
    }

    [Fact]
    public async Task Snapshot_ErroreDiRete_Neutro_ConTooltip_ENessunMessaggioDiErrore()
    {
        var f = new Fixture();
        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        f.Svn.HeadError = "svn: E170013: Unable to connect to a repository";
        await f.Store.HeadCheck.CheckAsync(Ct);

        var s = f.Snap;
        Assert.Equal("unknown", s.WorkingCopy.RevState);
        Assert.Null(s.WorkingCopy.Head);
        Assert.Contains("E170013", s.WorkingCopy.RevNote);
        Assert.False(s.Status.Err);   // nessun popup, nessun errore nella barra: solo l'indicatore neutro
    }

    [Fact]
    public async Task Snapshot_PrimaDelPrimoControllo_Neutro_ControlloInCorso()
    {
        var f = new Fixture();
        await f.Store.RefreshAsync(Ct, reloadBranch: true);

        var wc = f.Snap.WorkingCopy;
        Assert.Equal("unknown", wc.RevState);
        Assert.Contains("in corso", wc.RevNote);
    }

    [Fact]
    public async Task Commit_LaRevisioneDelCommitContaComeMinimo_NonDiventaArancioDaSola()
    {
        var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("a.txt", SvnStatusKind.Modified, IsTreeConflict: false));
        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        f.Store.CommitMessage.Text = "msg";

        await f.Cmd("commit");   // FakeSvnService.CommitAsync -> r42; svn info continua a dire 1234, ma 42 < 1234: resta 1234
        f.Svn.InfoRevision = 40;  // WC vecchia (mista): dopo il commit la radice resta alla r40, il commit e' r42
        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        f.Svn.HeadRevision = 42;  // il server: l'ultima modifica del ramo e' proprio il nostro commit
        await f.Store.HeadCheck.CheckAsync(Ct);

        var wc = f.Snap.WorkingCopy;
        Assert.Equal((42L, "current"), (wc.Rev, wc.RevState));
    }

    // ── Messaggi recenti ──

    [Fact]
    public void Recent_Massimo25_DuplicatiInCima_PerRepository()
    {
        var file = Path.Combine(Path.GetTempPath(), "SVNAssist_RecentMsg_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var r = new RecentMessages(file);
        for (var i = 1; i <= 30; i++)
            r.Add("repoA", "msg " + i);
        r.Add("repoB", "altro repo");

        var a = r.Get("repoA");
        Assert.Equal(25, a.Count);
        Assert.Equal("msg 30", a[0]);
        Assert.Equal("msg 6", a[^1]);   // le piu' vecchie escono
        Assert.Equal(["altro repo"], r.Get("repoB"));

        r.Add("repoA", "  msg 10 ");   // un duplicato (anche con spazi ai bordi) risale in cima, senza raddoppiare
        a = r.Get("repoA");
        Assert.Equal(25, a.Count);
        Assert.Equal(["msg 10", "msg 30", "msg 29"], a.Take(3));
        Assert.Equal(1, a.Count(m => m == "msg 10"));

        r.Add("repoA", "   ");   // vuoto: ignorato
        Assert.Equal(25, r.Get("repoA").Count);
        Assert.Equal(a, new RecentMessages(file).Get("repoA"));   // salvato su disco
        File.Delete(file);
    }

    [Fact]
    public void Recent_FileRovinatoOAssente_Vuoto_ENonLancia()
    {
        var file = Path.Combine(Path.GetTempPath(), "SVNAssist_RecentMsg_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Assert.Empty(new RecentMessages(file).Get("r"));
        File.WriteAllText(file, "{ non json");
        var r = new RecentMessages(file);
        Assert.Empty(r.Get("r"));
        r.Add("r", "ok");
        Assert.Equal(["ok"], r.Get("r"));
        File.Delete(file);
    }

    [Fact]
    public async Task Commit_RicordaIlMessaggio_PerIlRepository_ELaTendinaLoInserisce()
    {
        var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("a.txt", SvnStatusKind.Modified, IsTreeConflict: false));
        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        f.Store.CommitMessage.Text = "  Corregge il totale  ";
        await f.Cmd("commit");

        Assert.Equal(["Corregge il totale"], f.Snap.Commit.Recent);
        Assert.Equal(["Corregge il totale"], new RecentMessages(f.RecentFile).Get("file:///repo"));

        f.Store.CommitMessage.Text = "bozza";
        await f.Cmd("useRecentMessage", new { i = 0 });
        Assert.Equal("Corregge il totale", f.Store.CommitMessage.Text);

        await f.Cmd("useRecentMessage", new { i = 5 });   // indice fuori elenco: ignorato
        Assert.Equal("Corregge il totale", f.Store.CommitMessage.Text);
    }

    // ── Messaggio del merge, come TortoiseSVN ──

    [Theory]
    [InlineData("4,5,6", "4-6")]
    [InlineData("1,3,4,5", "1,3-5")]
    [InlineData("7", "7")]
    [InlineData("9,2,3,3", "2-3,9")]
    [InlineData("", "")]
    public void RevRanges_UnisceLeConsecutive(string csv, string expected)
        => Assert.Equal(expected, MergeStore.RevRanges(csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse)));

    [Fact]
    public void MergeCommitMessage_FormatoTortoiseSvn_ConUnaSezionePerRevisione()
    {
        var entries = new[]
        {
            new LogEntryDto(4, "mrossi", "", "Prima modifica", []),
            new LogEntryDto(5, "lbianchi", "", "Seconda\ncon dettaglio\n", []),
        };

        var text = MergeStore.MergeCommitMessage([4, 5, 6], entries, "branches/feature-demo");

        Assert.Equal("Unite le revisioni 4-6 da branches/feature-demo:\n........\nr4 | mrossi | Prima modifica\n........\nr5 | lbianchi | Seconda\ncon dettaglio\n........\nr6", text);
    }

    [Fact]
    public void MergeCommitMessage_InInglese_UsaLaFraseDiTortoiseSvn()
    {
        var old = Loc.Lang;
        Loc.Lang = "en";
        try
        {
            var text = MergeStore.MergeCommitMessage([4, 5, 6], [new LogEntryDto(4, "mrossi", "", "x", [])], "branches/feature-demo");
            Assert.StartsWith("Merged revision(s) 4-6 from branches/feature-demo:\n........\nr4 | mrossi | x", text);
        }
        finally
        {
            Loc.Lang = old;
        }
    }
}
