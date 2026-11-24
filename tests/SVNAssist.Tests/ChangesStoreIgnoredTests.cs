using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// 0.7.1: il watcher non ignora piu' cartelle scritte nel codice (bin, obj, Debug, Release, x64, packages...).
/// Decide svn:ignore dell'utente: le cartelle ignorate si chiedono a svn una volta e una raffica tutta sotto di loro non rilancia svn status.
/// </summary>
public sealed class ChangesStoreIgnoredTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _wc = Path.Combine(Path.GetTempPath(), "SVNAssist_Ign_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeSvnService _svn = new();
    private readonly ChangesStore _store;

    public ChangesStoreIgnoredTests()
    {
        Directory.CreateDirectory(Path.Combine(_wc, "src", "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(_wc, "src", "x64"));
        Directory.CreateDirectory(Path.Combine(_wc, "Logger", "Release"));
        var settings = new StubSettings();
        _store = new ChangesStore(settings, _svn, new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
            new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())))
        {
            WorkingCopyPath = _wc,
            TempRoot = Path.Combine(_wc, ".svn-assist", "tmp"),
        };
        _svn.IgnoredPaths.Add(P("src", "bin")); // svn:ignore = bin su src
    }

    private string P(params string[] parts) => Path.Combine([_wc, .. parts]);

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_wc, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SoloEventiSottoCartellaIgnorata_NienteSvnStatus_ESiChiedeUnaVolta()
    {
        _store.OnFsPath(P("src", "bin", "Debug", "app.dll"));
        _store.OnFsPath(P("src", "bin", "Debug", "app.pdb"));

        Assert.False(await _store.NeedsAutoRefreshAsync(_wc, Ct));
        var asked = Assert.Single(_svn.IgnoredRequests);
        // tutta la catena: svn non dice "ignorata" per src\bin\Debug, solo per src\bin
        Assert.Contains(P("src"), asked);
        Assert.Contains(P("src", "bin"), asked);
        Assert.Contains(P("src", "bin", "Debug"), asked);

        // Seconda raffica (build successiva): gia' nota, nessuna nuova domanda a svn.
        _store.OnFsPath(P("src", "bin", "Debug", "app.dll"));
        Assert.False(await _store.NeedsAutoRefreshAsync(_wc, Ct));
        Assert.Single(_svn.IgnoredRequests);
    }

    [Theory]
    [InlineData("src", "x64", "Kernel.cpp")]
    [InlineData("Logger", "Release", "Trace.cpp")]
    public async Task CartelleDebugReleaseX64NonIgnorate_FannoRefresh(string a, string b, string file)
    {
        _store.OnFsPath(P(a, b, file));
        Assert.True(await _store.NeedsAutoRefreshAsync(_wc, Ct));
    }

    [Fact]
    public async Task RafficaMista_FaRefresh()
    {
        _store.OnFsPath(P("src", "bin", "Debug", "app.dll"));
        _store.OnFsPath(P("src", "a.cs"));
        Assert.True(await _store.NeedsAutoRefreshAsync(_wc, Ct));
    }

    [Fact]
    public async Task RichiestaSenzaPercorso_RefreshCompletoSenzaChiedereASvn()
    {
        Assert.False(await _store.NeedsAutoRefreshAsync(_wc, Ct)); // nulla in attesa: nessun refresh
        _store.RequestFullRefresh(); // overflow del watcher, salvataggio in VS, rientro del focus
        Assert.True(await _store.NeedsAutoRefreshAsync(_wc, Ct));
        Assert.Empty(_svn.IgnoredRequests);
    }

    [Fact]
    public async Task ErroreDiSvn_FaRefreshComunque()
    {
        _svn.IgnoredThrows = true;
        _store.OnFsPath(P("src", "bin", "Debug", "app.dll"));
        Assert.True(await _store.NeedsAutoRefreshAsync(_wc, Ct));
    }

    [Fact]
    public async Task RefreshNonAutomatico_RiclassificaLeCartelle()
    {
        _store.OnFsPath(P("src", "bin", "Debug", "app.dll"));
        Assert.False(await _store.NeedsAutoRefreshAsync(_wc, Ct));

        // L'utente toglie bin da svn:ignore e preme Refresh (o un'operazione rilegge lo stato).
        _svn.IgnoredPaths.Clear();
        await _store.RefreshAsync(Ct);

        _store.OnFsPath(P("src", "bin", "Debug", "app.dll"));
        Assert.True(await _store.NeedsAutoRefreshAsync(_wc, Ct));
        Assert.Equal(2, _svn.IgnoredRequests.Count);
    }

    [Fact]
    public async Task EventiInSvnESvnAssist_ScartatiSubito()
    {
        _store.OnFsPath(P(".svn", "wc.db"));
        _store.OnFsPath(P(".svn-assist", "shelves", "x.patch"));
        Assert.False(await _store.NeedsAutoRefreshAsync(_wc, Ct));
        Assert.Empty(_svn.IgnoredRequests);
    }
}
