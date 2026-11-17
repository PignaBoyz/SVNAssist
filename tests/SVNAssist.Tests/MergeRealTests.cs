using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// Merge su repository file:// veri (svn installato): file coinvolti per revisione, anteprima per revisione e merge con modifiche pendenti
/// (backup, annulla che le ripristina). Dove svn non c'e' i test falliscono come gli altri "SvnVero".
/// </summary>
public class MergeRealTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Trunk (wc) con a.txt, b.txt, c.txt, sub/; branch feature con r4 (a.txt), r5 (new.txt), r6 (elimina b.txt), r7 (c.txt + proprieta' su sub).</summary>
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SVNAssist_MgReal2_" + Guid.NewGuid().ToString("N")[..8]);
        public string SvnExe { get; } = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        public string Url { get; private set; } = "";
        public string Wc { get; private set; } = "";
        public string Bwc { get; private set; } = "";
        public FakeDialogs Dialogs { get; } = new();
        public FakeHost Host { get; } = new();
        public ChangesStore Changes { get; private set; } = null!;
        public LogStore Log { get; private set; } = null!;
        public SvnService Svn { get; private set; } = null!;

        public string Run(string args, string? cwd = null, string? exe = null)
        {
            using var p = Process.Start(new ProcessStartInfo(exe ?? SvnExe, args)
            { WorkingDirectory = cwd ?? Wc, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Environment = { ["LC_ALL"] = "C" } })!;
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"svn {args}: {e}");
            return o;
        }

        public string Path_(string rel) => Path.Combine(Wc, rel.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string rel, string text) => File.WriteAllText(Path_(rel), text);

        public string Read(string rel) => File.ReadAllText(Path_(rel));

        public Env()
        {
            Directory.CreateDirectory(Root);
            var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
            var repo = Path.Combine(Root, "repo");
            Url = "file:///" + repo.Replace('\\', '/');
            Run($"create \"{repo}\"", Root, admin);
            Run($"mkdir -m init \"{Url}/trunk\" \"{Url}/branches\" \"{Url}/tags\"", Root);
            Wc = Path.Combine(Root, "wc");
            Run($"checkout \"{Url}/trunk\" \"{Wc}\"", Root);
            Write("a.txt", "line1\nline2\nline3\n");
            Write("b.txt", "bee\n");
            Write("c.txt", "cee\n");
            Directory.CreateDirectory(Path_("sub"));
            Run("add a.txt b.txt c.txt sub");
            Run("commit -m base");                                                          // r2
            Run($"copy \"{Url}/trunk\" \"{Url}/branches/feature\" -m branch", Root);          // r3
            Bwc = Path.Combine(Root, "bwc");
            Run($"checkout \"{Url}/branches/feature\" \"{Bwc}\"", Root);
            File.WriteAllText(Path.Combine(Bwc, "a.txt"), "line1\nline2 BRANCH\nline3\n");
            Run("commit -m \"feat: modifica a\"", Bwc);                                      // r4
            File.WriteAllText(Path.Combine(Bwc, "new.txt"), "new\n");
            Run("add new.txt", Bwc);
            Run("commit -m \"feat: aggiunge new\"", Bwc);                                    // r5
            Run("rm b.txt", Bwc);
            Run("commit -m \"feat: elimina b\"", Bwc);                                       // r6
            File.WriteAllText(Path.Combine(Bwc, "c.txt"), "cee BRANCH\n");
            Run("propset myprop 1 sub", Bwc);
            Run("commit -m \"feat: c e proprieta su sub\"", Bwc);                            // r7
            Run("update");
            Svn = new SvnService(SvnExe);
            var settings = new StubSettings();
            Changes = new ChangesStore(settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), recentTargetsFile: Path.Combine(Root, "recent.json"))
            { WorkingCopyPath = Wc, TempRoot = Path.Combine(Root, "tmp"), Recent = new RecentMessages(Path.Combine(Root, "recentmsg.json")) };
            Log = new LogStore(Svn, settings, Host, null, Changes);
            Changes.StartMerge = Log.StartMergeAsync;
            Changes.MergeUndone = ct => Log.Merge!.RefreshAsync(ct);
        }

        public Task Cmd(string name, object? args = null) => Changes.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public Task LogCmd(string name, object? args = null) => Log.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public LogState LogSnap => (LogState)Log.GetSnapshot();

        public MergeDto Merge => LogSnap.Merge!;

        public async Task OpenMergeAsync()
        {
            await Changes.RefreshAsync(Ct);
            await Cmd("mergeOpen");
            await Cmd("switchOk", new { name = "branches/feature" });
        }

        public async Task<MergeFilesDto> FilesAsync()
        {
            for (var i = 0; i < 300; i++)
            {
                if (Merge.Files is { Loading: false } f)
                    return f;
                await Task.Delay(20);
            }

            throw new TimeoutException("file non arrivati");
        }

        public void Dispose()
        {
            Changes.Dispose();
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch { /* .svn in sola lettura */ }
        }
    }

    [Fact]
    public async Task SvnVero_FileCoinvolti_PerLaScelta_Unione_DiffDiUnFile_EAnteprimaPerRevisione()
    {
        using var e = new Env();
        MergeStore.FilesDebounce = TimeSpan.Zero;
        await e.OpenMergeAsync();
        Assert.Equal([7L, 6, 5, 4], e.LogSnap.Entries.Select(x => x.Rev));
        Assert.All(e.LogSnap.Entries, x => Assert.Empty(x.Paths));   // niente -v nella lista

        await e.LogCmd("mergeToggle", new { rev = 4 });
        await e.LogCmd("mergeToggle", new { rev = 6 });
        await e.LogCmd("mergeToggle", new { rev = 7 });
        var files = await e.FilesAsync();

        Assert.Equal(["M a.txt", "D b.txt", "M c.txt", "M sub"], files.Items.Select(i => $"{i.A} {i.P}"));
        Assert.Equal("dir", files.Items.Single(i => i.P == "sub").K);
        Assert.Equal(3, files.Files);

        await e.LogCmd("mergeFileOpen", new { p = "a.txt" });
        var diff = e.Host.Diffs.Single();
        Assert.Equal("a.txt r3 ↔ r4", diff.Caption);
        Assert.Contains("BRANCH", File.ReadAllText(diff.Right));
        Assert.DoesNotContain("BRANCH", File.ReadAllText(diff.Left));

        await e.LogCmd("mergeFileOpen", new { p = "b.txt" });   // eliminato in r6: destra vuota
        Assert.Equal(string.Empty, File.ReadAllText(e.Host.Diffs[1].Right));

        await e.LogCmd("mergePreview");
        var by = e.Merge.Result!.ByRev!;
        Assert.Equal([4L, 6, 7], by.Select(g => g.Rev));
        Assert.Equal(["M a.txt"], by[0].Files.Select(f => $"{f.A} {f.P}"));
        Assert.Equal(["D b.txt"], by[1].Files.Select(f => $"{f.A} {f.P}"));
        Assert.Equal("feat: modifica a", by[0].Msg);
        Assert.Empty(await e.Svn.GetLocalChangesAsync(e.Wc, Ct));   // l'anteprima non tocca la working copy
    }
}
