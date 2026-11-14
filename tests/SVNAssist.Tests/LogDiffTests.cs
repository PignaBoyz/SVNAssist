using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Filtri del log, argomenti di svn, scelta delle revisioni del diff e diff nativo da Log.</summary>
public class LogDiffTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SVNAssist_LD_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeSvnService _svn = new() { LogEntryCount = 3 };
    private readonly FakeHost _host = new();
    private readonly LogStore _store;

    public LogDiffTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wc", ".svn"));
        Directory.CreateDirectory(Path.Combine(_root, "wc", "src"));
        File.WriteAllText(Path.Combine(_root, "wc", "src", "a.cs"), "x");
        _store = new LogStore(_svn, new StubSettings(), _host, Path.Combine(_root, "tmp"));
    }

    public void Dispose()
    {
        _store.CleanTemp();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Task Cmd(string name, object? args = null) => _store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
    private LogState Snap => (LogState)_store.GetSnapshot();
    private string Wc => Path.Combine(_root, "wc");

    // ── argomenti di svn log ─────────────────────────────────────────

    [Fact]
    public void BuildLogArgs_SenzaFiltro_EQuelliDiSempre()
        => Assert.Equal(["log", "--xml", "-v", "-l", "50", "C:\\wc"], SvnService.BuildLogArgs("C:\\wc", 50, new SvnLogFilter()));

    [Fact]
    public void BuildLogArgs_StopOnCopyEMerged_AggiungonoLeOpzioni()
    {
        var a = SvnService.BuildLogArgs("C:\\wc", 50, new SvnLogFilter(StopOnCopy: true, IncludeMerged: true));
        Assert.Equal(["log", "--xml", "-v", "-l", "50", "--stop-on-copy", "-g", "C:\\wc"], a);
    }

    [Fact]
    public async Task LogOptions_DefaultStopOnCopy_PoiLeOpzioniArrivanoAlBackendERicaricano()
    {
        await _store.DispatchAsync("logLoad", JsonSerializer.SerializeToElement(new { path = Wc }), Ct);
        Assert.True(_svn.LastLogFilter!.StopOnCopy);
        Assert.False(_svn.LastLogFilter.IncludeMerged);
        Assert.True(Snap.StopOnCopy);

        await Cmd("logOptions", new { stopOnCopy = false, merged = true });
        Assert.False(_svn.LastLogFilter.StopOnCopy);
        Assert.True(_svn.LastLogFilter.IncludeMerged);
        Assert.False(Snap.StopOnCopy);
        Assert.True(Snap.IncludeMerged);
    }

    [Fact]
    public void Flatten_RevisioniUnite_SonoIndentateSottoIlMerge()
    {
        var child = new SvnLogEntry(7, "b", DateTimeOffset.UnixEpoch, "x", [], null, ReverseMerge: true);
        var top = new SvnLogEntry(9, "a", DateTimeOffset.UnixEpoch, "merge", [], [child]);
        var rows = LogStore.Flatten(top, 0).ToList();
        Assert.Equal([9L, 7L], rows.Select(r => r.Rev));
        Assert.Equal([0, 1], rows.Select(r => r.Depth));
        Assert.Equal([false, true], rows.Select(r => r.Reverse));
    }

    [Fact]
    public void BuildLogArgs_AutoreTestoPercorso_DiventanoSearchInAnd()
    {
        var a = SvnService.BuildLogArgs("C:\\wc", 50, new SvnLogFilter("mario", "fix [bug]", "src\\Core"));
        Assert.Equal(["--search", "*mario*", "--search-and", "*fix \\[bug]*", "--search-and", "*src/Core*"], a.Skip(5).Take(6));
        Assert.Equal("C:\\wc", a[^1]);
    }

    [Fact]
    public void BuildLogArgs_Date_DiventanoUnIntervalloDiRevisioni()
    {
        var from = new DateOnly(2024, 1, 5);
        var to = new DateOnly(2024, 2, 9);
        Assert.Contains("{2024-02-09T23:59:59}:{2024-01-05T00:00:00}", SvnService.BuildLogArgs("t", 5, new SvnLogFilter(From: from, To: to)));
        Assert.Contains("HEAD:{2024-01-05T00:00:00}", SvnService.BuildLogArgs("t", 5, new SvnLogFilter(From: from)));
        Assert.Contains("{2024-02-09T23:59:59}:1", SvnService.BuildLogArgs("t", 5, new SvnLogFilter(To: to)));
    }

    [Fact]
    public void BuildLogArgs_TargetCheIniziaConTrattino_NonEUnOpzione()
        => Assert.StartsWith(".", SvnService.BuildLogArgs("-x", 5, new SvnLogFilter())[^1]);

    // ── filtro in memoria ────────────────────────────────────────────

    private static SvnLogEntry Entry(string author, string msg, string path, int day = 10)
        => new(1, author, new DateTimeOffset(2024, 3, day, 12, 0, 0, TimeSpan.Zero), msg, [new SvnLogPath("M", path)]);

    [Fact]
    public void Matches_AutoreTestoPercorso_SenzaMaiuscole()
    {
        var e = Entry("Mario", "Corretto il BUG del login", "/trunk/src/Login.cs");
        Assert.True(new SvnLogFilter("mar", "bug", "SRC\\login").Matches(e));
        Assert.False(new SvnLogFilter("luigi").Matches(e));
        Assert.False(new SvnLogFilter(Text: "logout").Matches(e));
        Assert.False(new SvnLogFilter(Path: "docs/").Matches(e));
    }

    [Fact]
    public void Matches_Date_Inclusive()
    {
        var e = Entry("u", "m", "/a", day: 10);
        Assert.True(new SvnLogFilter(From: new DateOnly(2024, 3, 10), To: new DateOnly(2024, 3, 10)).Matches(e));
        Assert.False(new SvnLogFilter(From: new DateOnly(2024, 3, 11)).Matches(e));
        Assert.False(new SvnLogFilter(To: new DateOnly(2024, 3, 9)).Matches(e));
    }

    [Fact]
    public void ParseSummarizeXml_SoloFile_ConAzione()
    {
        const string xml = """
            <diff><paths>
              <path item="modified" kind="file" props="none">C:\wc\a.cs</path>
              <path item="added" kind="file" props="none">C:\wc\b.cs</path>
              <path item="deleted" kind="file" props="none">C:\wc\c.cs</path>
              <path item="modified" kind="dir" props="modified">C:\wc</path>
            </paths></diff>
            """;
        Assert.Equal([new SvnChange("M", @"C:\wc\a.cs"), new SvnChange("A", @"C:\wc\b.cs"), new SvnChange("D", @"C:\wc\c.cs")], SvnService.ParseSummarizeXml(xml));
    }

    // ── scelta delle revisioni del diff ──────────────────────────────

    [Theory]
    [InlineData("M", 13L, 12L, 13L)]
    [InlineData("R", 13L, 12L, 13L)]
    public void ForRevision_Modificato_PrecedenteControRevisione(string a, long rev, long l, long r)
        => Assert.Equal((l, r), DiffOpener.ForRevision(a, rev));

    [Fact]
    public void ForRevision_AggiuntoEEliminato_UnLatoVuoto()
    {
        Assert.Equal((null, 13L), DiffOpener.ForRevision("A", 13));
        Assert.Equal((12L, null), DiffOpener.ForRevision("D", 13));
    }

    [Fact]
    public void ForRange_Confronto_AEB()
    {
        Assert.Equal((10L, 15L), DiffOpener.ForRange("M", 10, 15));
        Assert.Equal((null, 15L), DiffOpener.ForRange("A", 10, 15));
        Assert.Equal((10L, null), DiffOpener.ForRange("D", 10, 15));
    }

    [Fact]
    public void Title_Chiaro()
    {
        Assert.Equal("Program.cs r12 ↔ r13", DiffOpener.Title("Program.cs", 12, 13));
        Assert.Equal("Program.cs (vuoto) ↔ r13", DiffOpener.Title("Program.cs", null, 13));
    }

    // ── LogStore: filtri ─────────────────────────────────────────────

    [Fact]
    public async Task LogFilter_PassaIlFiltroASvn_ERiportaLoStatoNellaUi()
    {
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        await Cmd("logFilter", new { author = " mario ", text = "bug", path = "src", from = "2024-05-01", to = "2024-04-01" });

        Assert.Equal("mario", _svn.LastLogFilter!.Author);
        Assert.Equal(new DateOnly(2024, 4, 1), _svn.LastLogFilter.From); // date invertite: si scambiano
        Assert.Equal(new DateOnly(2024, 5, 1), _svn.LastLogFilter.To);
        Assert.Equal("2024-04-01", Snap.Filter.From);
        Assert.Contains("filtro attivo", Snap.Msg);

        await Cmd("logMore"); // la paginazione mantiene il filtro
        Assert.Equal("src", _svn.LastLogFilter!.Path);
        Assert.Equal(100, _svn.LogRequestedLimit);
    }

    [Fact]
    public async Task LogFilter_Vuoto_AzzeraIlFiltro()
    {
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        await Cmd("logFilter", new { author = "x" });
        await Cmd("logFilter", new { });
        Assert.True(_svn.LastLogFilter!.IsEmpty);
    }

    // ── LogStore: diff nativo ────────────────────────────────────────

    [Fact]
    public async Task LogOpenFile_ApreIlDiffNativo_RevPrecedenteControRevisione()
    {
        _svn.LogPaths.Add(new SvnLogPath("M", "/trunk/src/Program.cs", "file"));
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        await Cmd("logOpenFile", new { rev = 3, i = 0 });

        Assert.Equal([("file:///repo/trunk/src/Program.cs", 2L), ("file:///repo/trunk/src/Program.cs", 3L)],
            _svn.ExportedRevisions.Select(x => (x.Target, x.Rev)));
        var d = _host.Diffs.Single();
        Assert.Equal("Program.cs r2 ↔ r3", d.Caption);
        Assert.EndsWith("Program.cs", d.Left);
    }

    [Fact]
    public async Task LogOpenFile_FileAggiunto_LatoSinistroVuoto()
    {
        _svn.LogPaths.Add(new SvnLogPath("A", "/trunk/New.cs", "file"));
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        await Cmd("logOpenFile", new { rev = 2, i = 0 });

        Assert.Single(_svn.ExportedRevisions);
        Assert.Equal(string.Empty, File.ReadAllText(_host.Diffs.Single().Left));
        Assert.Equal("New.cs (vuoto) ↔ r2", _host.Diffs.Single().Caption);
    }

    [Fact]
    public async Task LogOpenFile_Cartella_OIndiceSbagliato_DiventaUnMessaggio()
    {
        _svn.LogPaths.Add(new SvnLogPath("M", "/trunk/src", "dir"));
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);

        await Cmd("logOpenFile", new { rev = 1, i = 0 });
        Assert.True(Snap.Err);
        await Cmd("logOpenFile", new { rev = 1, i = 7 });
        Assert.True(Snap.Err);
        await Cmd("logOpenFile", new { rev = 99, i = 0 });
        Assert.True(Snap.Err);
        Assert.Empty(_host.Diffs);
    }

    [Fact]
    public async Task LogCompare_ElencaIFileEApreIlDiffTraLeDueRevisioni()
    {
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        _svn.Changed.Add(new SvnChange("M", Path.Combine(Wc, "src", "a.cs")));
        await Cmd("logCompare", new { a = 3, b = 1 });

        Assert.Equal("compare", Snap.Files!.Kind);
        Assert.Equal("src\\a.cs", Snap.Files.Items.Single().P);

        await Cmd("logFileOpen", new { i = 0 });
        Assert.Equal([1L, 3L], _svn.ExportedRevisions.Select(x => x.Rev));
        Assert.Equal("a.cs r1 ↔ r3", _host.Diffs.Single().Caption);

        await Cmd("logSelect", new { rev = 2 });
        Assert.Null(Snap.Files);
    }

    [Fact]
    public async Task LogFolder_ElencaIModificatiEApreBaseControWorkingCopy_EPatch()
    {
        _svn.Statuses.Add(new SvnFileStatus("a.cs", SvnStatusKind.Modified));
        _svn.Statuses.Add(new SvnFileStatus("n.cs", SvnStatusKind.Unversioned));
        await Cmd("logFolder", new { path = Path.Combine(Wc, "src") });

        Assert.Equal("folder", Snap.Files!.Kind);
        Assert.Equal("a.cs", Snap.Files.Items.Single().P);

        await Cmd("logFileOpen", new { i = 0 });
        Assert.Contains("BASE", _host.Diffs.Single().Caption);
        Assert.Equal(Path.Combine(Wc, "src", "a.cs"), _host.Diffs.Single().Right);

        await Cmd("logFilesPatch");
        Assert.EndsWith(".diff", _host.OpenedFiles.Single());
    }

    [Fact]
    public async Task LogFolder_FuoriDaUnaWorkingCopy_Rifiutata()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        await Cmd("logFolder", new { path = Path.Combine(_root, "plain") });
        Assert.True(Snap.Err);
        Assert.Null(Snap.Files);
    }

    [Fact]
    public async Task CleanTemp_CancellaIFileTemporaneiDeiDiff()
    {
        _svn.LogPaths.Add(new SvnLogPath("M", "/trunk/Program.cs", "file"));
        await _store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        await Cmd("logOpenFile", new { rev = 3, i = 0 });
        var left = _host.Diffs.Single().Left;
        Assert.True(File.Exists(left));

        _store.CleanTemp();
        Assert.False(File.Exists(left));
    }
}
