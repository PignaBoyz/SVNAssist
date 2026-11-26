using System.Diagnostics;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// "Show Log righe selezionate" su un repository file:// vero: riga modificata su un branch, arrivata nel ramo aperto con due
/// merge a catena (feat -> trunk -> rel), riga aggiunta sopra (che sposta i numeri), riga non ancora committata.
/// </summary>
public class LineHistoryRealTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SVNAssist_LineHist_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _svn = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
    private readonly string _url;
    private readonly string _rel; // working copy di branches/rel

    private string Run(string args, string cwd, string? exe = null)
    {
        using var p = Process.Start(new ProcessStartInfo(exe ?? _svn, args)
        { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Environment = { ["LC_ALL"] = "C" } })!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"svn {args}: {e}");
        return o;
    }

    public LineHistoryRealTests()
    {
        Directory.CreateDirectory(_root);
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var repo = Path.Combine(_root, "repo");
        _url = "file:///" + repo.Replace('\\', '/').TrimStart('/');
        Run($"create \"{repo}\"", _root, admin);
        Run($"mkdir -m init \"{_url}/trunk\" \"{_url}/branches\"", _root);                    // r1
        var trunk = Path.Combine(_root, "trunk");
        Run($"checkout \"{_url}/trunk\" \"{trunk}\"", _root);
        Directory.CreateDirectory(Path.Combine(trunk, "src"));
        File.WriteAllText(Path.Combine(trunk, "src", "f.txt"), "a\nb\nc\nd\n");
        Run("add src", trunk);
        Run("commit -m \"crea f\" --username anna", trunk);                                    // r2
        Run($"copy -m feat \"{_url}/trunk\" \"{_url}/branches/feat\"", _root);                 // r3
        Run($"copy -m rel \"{_url}/trunk\" \"{_url}/branches/rel\"", _root);                   // r4
        var feat = Path.Combine(_root, "feat");
        Run($"checkout \"{_url}/branches/feat\" \"{feat}\"", _root);
        File.WriteAllText(Path.Combine(feat, "src", "f.txt"), "a\nb\nC60\nd\n");
        Run("commit -m \"fix: timeout\" --username luca", feat);                               // r5
        Run("update", trunk);
        Run($"merge \"{_url}/branches/feat\" .", trunk);
        Run("commit -m \"merge feat\" --username marco", trunk);                               // r6
        _rel = Path.Combine(_root, "rel");
        Run($"checkout \"{_url}/branches/rel\" \"{_rel}\"", _root);
        File.WriteAllText(Path.Combine(_rel, "src", "f.txt"), "x\na\nb\nc\nd\n");
        Run("commit -m \"riga sopra\" --username ugo", _rel);                                  // r7
        Run("update", _rel);
        Run($"merge \"{_url}/trunk\" .", _rel);
        Run("commit -m \"merge trunk\" --username marco", _rel);                               // r8
        Run("update", _rel);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string File_ => Path.Combine(_rel, "src", "f.txt");

    private async Task<(LogState State, FakeHost Host)> LineLogAsync(int from, int to)
    {
        var host = new FakeHost();
        var log = new LogStore(new SvnService(_svn), new StubSettings(), host);
        await log.StartLineHistoryAsync(File_, from, to, Ct);
        return ((LogState)log.GetSnapshot(), host);
    }

    [Fact]
    public async Task RigaArrivataConDueMerge_MostraIlMergeLeRevisioniOriginaliELaCreazione()
    {
        Assert.Equal("C60", File.ReadAllLines(File_)[3]);

        var (s, host) = await LineLogAsync(4, 4);

        Assert.False(s.Err, s.Msg);
        Assert.Equal(File_, s.Path);
        // r8 (merge nel ramo) con dentro r6 (merge in trunk) e r5 (la modifica vera su feat); r2 crea la riga.
        // Fuori: r7 (tocca un'altra riga), r3/r4 (copie dei branch).
        Assert.Equal([8L, 6L, 5L, 2L], s.Entries.Select(e => e.Rev));
        Assert.Equal([0, 1, 2, 0], s.Entries.Select(e => e.Depth));
        Assert.Equal("luca", s.Entries.Single(e => e.Rev == 5).Author);
        Assert.False(s.CanMore);
        Assert.Contains("f.txt", Assert.Single(host.LogWindowTitles));
    }

    [Fact]
    public async Task RigaAggiuntaSopra_HaSoloLaSuaRevisione()
    {
        var (s, _) = await LineLogAsync(1, 1);

        Assert.Equal([7L], s.Entries.Select(e => e.Rev));
    }

    [Fact]
    public async Task RigaNonCommittata_NessunaRevisioneEMessaggio()
    {
        File.AppendAllText(File_, "nuova\n");

        var (s, _) = await LineLogAsync(6, 6);

        Assert.Empty(s.Entries);
        Assert.False(s.Err);
        Assert.Contains("committ", s.Msg);
    }

    [Fact]
    public async Task ModificaLocaleSopra_LeRigheSiRiportanoSullaBase()
    {
        File.WriteAllText(File_, "x\nnuova1\nnuova2\na\nb\nC60\nd\n");

        var (s, _) = await LineLogAsync(6, 6); // "C60", che sulla BASE e' la riga 4

        Assert.Equal([8L, 6L, 5L, 2L], s.Entries.Select(e => e.Rev));
    }
}
