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

        Assert.Equal(["Alfa", "zeta"], SvnService.ParseListXml(xml));
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
    public async Task ListFoldersAsync_PercorsoInesistente_ElencoVuotoNonErrore()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(1, "", "svn: E170000: URL 'https://x/repo/tags' doesn't exist") };

        Assert.Empty(await new SvnService(FakeSvnPath, runner).ListFoldersAsync("https://x/repo/tags", Ct));
    }

    [Fact]
    public async Task ListFoldersAsync_AltriErrori_Propagano()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(1, "", "svn: E170013: Unable to connect to a repository") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SvnService(FakeSvnPath, runner).ListFoldersAsync("https://x/repo/tags", Ct));
    }

    [Fact]
    public async Task ListFoldersAsync_UsaListXml()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(0, "<lists><list><entry kind=\"dir\"><name>a</name></entry></list></lists>", "") };

        Assert.Equal(["a"], await new SvnService(FakeSvnPath, runner).ListFoldersAsync("https://x/repo/branches", Ct));
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

        public Fixture(string relativeUrl = "^/trunk")
        {
            Svn.InfoRelativeUrl = relativeUrl;
            Svn.Folders["file:///repo"] = ["branches", "tags", "trunk"];
            Svn.Folders["file:///repo/branches"] = ["feature-x", "con spazio"];
            Svn.Folders["file:///repo/tags"] = ["1.0"];
            Store = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, new FakeHost(),
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())))
            { WorkingCopyPath = @"C:\wc", TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Sw_" + Guid.NewGuid().ToString("N")[..8]) };
        }

        public SwitchPickerDto Picker => ((ChangesState)Store.GetSnapshot()).SwitchPicker;
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

        f.Dialogs.ConfirmResult = false;
        await f.Cmd("switchOk", new { name = "tags/1.0" });
        Assert.Empty(f.Svn.Switched);
        Assert.True(f.Picker.Open);   // annullato: il selettore resta aperto
        Assert.Single(f.Dialogs.Messages);

        f.Dialogs.ConfirmResult = true;
        await f.Cmd("switchOk", new { name = "tags/1.0" });
        Assert.Single(f.Svn.Switched);
    }

    [Fact]
    public async Task Switch_SoloNonVersionati_NonChiedeConferma()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\nuovo.txt", SvnStatusKind.Unversioned, IsTreeConflict: false));
        await f.Store.RefreshAsync(Ct);
        await f.Cmd("switchOpen");

        await f.Cmd("switchOk", new { name = "tags/1.0" });

        Assert.Empty(f.Dialogs.Messages);
        Assert.Single(f.Svn.Switched);
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
}
