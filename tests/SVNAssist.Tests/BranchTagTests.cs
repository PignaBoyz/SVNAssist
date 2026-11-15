using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>SVN Branch/Tag (0.5.0): nome, argomenti di svn copy, dialogo e flusso, rilevamento automatico del layout.</summary>
public class BranchTagTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("feature-x", true)]
    [InlineData("release/1.0", true)]
    [InlineData("", false)]
    [InlineData(" x", false)]
    [InlineData("x ", false)]
    [InlineData("a:b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a//b", false)]
    [InlineData("/a", false)]
    [InlineData("..", false)]
    public void ValidateName_Regole(string name, bool ok) => Assert.Equal(ok, BranchTagStore.ValidateName(name) is null);

    [Fact]
    public void BuildCopyArgs_WorkingCopyODaUrlConRevisione()
    {
        Assert.Equal(["copy", "--parents", "-F", "m.txt", "--encoding", "utf-8", @"C:\wc", "https://x/r/branches/b"], SvnService.BuildCopyArgs(@"C:\wc", "https://x/r/branches/b", "m.txt", null));
        Assert.Equal(["copy", "--parents", "-F", "m.txt", "--encoding", "utf-8", "-r", "12", "https://x/r/trunk", "https://x/r/tags/t"], SvnService.BuildCopyArgs("https://x/r/trunk", "https://x/r/tags/t", "m.txt", 12));
    }

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public ChangesStore Store { get; }

        public Fixture(string relativeUrl = "^/trunk")
        {
            Svn.InfoRelativeUrl = relativeUrl;
            Svn.Folders["file:///repo"] = ["branches", "tags", "trunk"];
            Svn.Folders["file:///repo/branches"] = ["esiste"];
            var recent = Path.Combine(Path.GetTempPath(), "SVNAssist_Bt_" + Guid.NewGuid().ToString("N")[..8] + ".json");
            Store = new ChangesStore(new StubSettings(), Svn, new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
                new PrereqStore(new PrerequisitesService(new StubSettings(), new PrerequisiteProbes())), recentTargetsFile: recent)
            { WorkingCopyPath = @"C:\wc", SwitchRetryDelay = TimeSpan.Zero, TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Bt_" + Guid.NewGuid().ToString("N")[..8]) };
        }

        public BranchTagDto? Dlg => ((ChangesState)Store.GetSnapshot()).BranchTag;
        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
        public Task Ok(string name, string kind = "branch", string source = "head", string rev = "", bool sw = false, string message = "")
            => Cmd("branchTagOk", new { kind, name, message, source, rev, @switch = sw });
        public void Dispose() => Store.Dispose();
    }

    [Fact]
    public async Task Apri_MostraRamoRevisioneECartelleDelLayout()
    {
        using var f = new Fixture("^/progetto/branches/b1");
        await f.Cmd("branchTagOpen", new { kind = "tag" });

        var d = f.Dlg!;
        Assert.Equal(("tag", "branches/b1", "progetto/branches", "progetto/tags"), (d.Kind, d.From, d.Branches, d.Tags));
    }

    [Fact]
    public async Task Crea_Branch_CopiaDaUrlDellaWorkingCopyEChiudeIlDialogo()
    {
        using var f = new Fixture();
        await f.Cmd("branchTagOpen", new { kind = "branch" });

        await f.Ok("feature y", message: "msg");

        Assert.Null(f.Dlg);
        var c = Assert.Single(f.Svn.Copies);
        Assert.Equal(("file:///repo/trunk", "file:///repo/branches/feature%20y", "msg", (long?)null), c);
        Assert.Empty(f.Svn.Switched);
    }

    [Fact]
    public async Task Crea_TagDaRevisioneConSwitch_UsaLoSwitchEsistente()
    {
        using var f = new Fixture();
        await f.Cmd("branchTagOpen", new { kind = "tag" });

        await f.Ok("1.1", "tag", "rev", "r5", sw: true);

        Assert.Equal(("file:///repo/trunk", "file:///repo/tags/1.1", "Crea tag tags/1.1", (long?)5), f.Svn.Copies.Single());
        Assert.Equal("file:///repo/tags/1.1", f.Svn.Switched.Single().Url);
    }

    [Fact]
    public async Task Crea_DallaWorkingCopy_SorgenteELIPercorso()
    {
        using var f = new Fixture();
        await f.Cmd("branchTagOpen", new { kind = "branch" });

        await f.Ok("b", source: "wc");

        Assert.Equal(@"C:\wc", f.Svn.Copies.Single().Source);
    }

    [Theory]
    [InlineData("esiste", "esiste già")]
    [InlineData(" spazio", "spazi")]
    [InlineData("", "nome")]
    public async Task NomeNonValidoOEsistente_ErroreChiaroNienteCopia(string name, string contains)
    {
        using var f = new Fixture();
        await f.Cmd("branchTagOpen", new { kind = "branch" });

        await f.Ok(name);

        Assert.Contains(contains, f.Dlg!.Error);
        Assert.Empty(f.Svn.Copies);
    }

    [Fact]
    public async Task RevisioneNonValida_Errore()
    {
        using var f = new Fixture();
        await f.Cmd("branchTagOpen", new { kind = "branch" });

        await f.Ok("b", source: "rev", rev: "abc");

        Assert.Contains("revisione", f.Dlg!.Error);
    }

    [Fact]
    public async Task ErroreDiSvnCopy_IlDialogoResta()
    {
        using var f = new Fixture();
        f.Svn.CopyError = "E170001 negato";
        await f.Cmd("branchTagOpen", new { kind = "branch" });

        await f.Ok("b");

        Assert.Contains("E170001", f.Dlg!.Error);
        Assert.False(f.Dlg.Busy);
    }

    // ── Layout automatico ────────────────────────────────────────────

    [Fact]
    public async Task Layout_NonCorrispondeAlleOptions_LoDeduceDalServer()
    {
        var svn = new FakeSvnService { InfoRelativeUrl = "^/proj/main/src" };
        svn.Folders["file:///repo"] = ["proj"];
        svn.Folders["file:///repo/proj"] = ["main", "branch", "tag"];

        var layout = await new RepoLayoutResolver(svn, new StubSettings()).ResolveAsync(await svn.GetInfoAsync("", Ct), Ct);

        Assert.Equal(new RepoLayout("main", "branch", "tag"), layout);
    }

    [Fact]
    public async Task Layout_ConfiguratoCorrisponde_NienteChiamateAlServer()
    {
        var svn = new FakeSvnService { InfoRelativeUrl = "^/trunk" };

        var layout = await new RepoLayoutResolver(svn, new StubSettings()).ResolveAsync(await svn.GetInfoAsync("", Ct), Ct);

        Assert.Equal(new RepoLayout("trunk", "branches", "tags"), layout);
        Assert.Empty(svn.Listed);
    }

    [Fact]
    public async Task Layout_NienteDaDedurre_RestaIlConfigurato()
    {
        var svn = new FakeSvnService { InfoRelativeUrl = "^/qualcosa" };
        svn.Folders["file:///repo"] = ["qualcosa", "altro"];

        var layout = await new RepoLayoutResolver(svn, new StubSettings()).ResolveAsync(await svn.GetInfoAsync("", Ct), Ct);

        Assert.Equal(new RepoLayout("trunk", "branches", "tags"), layout);
    }

    [Fact]
    public async Task Selettore_UsaIlLayoutDedotto()
    {
        using var f = new Fixture("^/proj/main");
        f.Svn.Folders["file:///repo"] = ["proj"];
        f.Svn.Folders["file:///repo/proj"] = ["main", "branch"];
        f.Svn.Folders["file:///repo/proj/branch"] = ["b1"];

        await f.Cmd("switchOpen");

        var picker = ((ChangesState)f.Store.GetSnapshot()).SwitchPicker;
        Assert.Equal(["main", "branch/b1"], picker.Items.Select(i => i.Name));
    }
}

/// <summary>Branch/Tag con svn vero su un repository file://: crea il branch, ne controlla il commit e lo switch.</summary>
public class BranchTagRealTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static void Run(string exe, string args, string cwd)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{args}: {e}");
    }

    [Fact]
    public async Task SvnVero_CreaBranchDallaWorkingCopyEPassaSubito()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_BtReal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            var wc = Path.Combine(root, "wc");
            Run(svnExe, $"mkdir -m base --parents \"{url}/trunk\"", root);
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc}\"", root);
            File.WriteAllText(Path.Combine(wc, "a.txt"), "x\n");
            Run(svnExe, "add a.txt", wc);
            Run(svnExe, "commit -m a", wc);

            var settings = new StubSettings();
            var svn = new SvnService(svnExe);
            using var store = new ChangesStore(settings, svn, new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), recentTargetsFile: Path.Combine(root, "recent.json"))
            { WorkingCopyPath = wc, TempRoot = Path.Combine(root, "tmp") };
            await store.RefreshAsync(Ct);
            JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

            await store.DispatchAsync("branchTagOpen", J(new { kind = "branch" }), Ct);
            await store.DispatchAsync("branchTagOk", J(new { kind = "branch", name = "feature è", message = "Crea branch", source = "head", rev = "", @switch = true }), Ct);

            Assert.Null(((ChangesState)store.GetSnapshot()).BranchTag);
            Assert.Contains("feature è", (await svn.ListEntriesAsync(url + "/branches", Ct)).Select(e => e.Name));
            Assert.EndsWith("/branches/feature%20%C3%A8", (await svn.GetInfoAsync(wc, Ct)).Url, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
