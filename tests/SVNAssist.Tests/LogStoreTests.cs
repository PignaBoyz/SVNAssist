using System.Text.Json;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Store della vista Log con svn finto.</summary>
public class LogStoreTests
{
    private const string Wc = @"C:\wc";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static Task Cmd(LogStore store, string name, object? args = null)
        => store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

    private static LogState Snap(LogStore store) => (LogState)store.GetSnapshot();

    /// <summary>Una working copy vera (con cartella .svn) per il percorso digitato dall'utente.</summary>
    private static string FakeWorkingCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SVNAssist_Log_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, ".svn"));
        return dir;
    }

    [Fact]
    public async Task LogLoad_Poi_CaricaAltre50_AumentaIlLimite()
    {
        var svn = new FakeSvnService();
        var store = new LogStore(svn, new StubSettings());
        var wc = FakeWorkingCopy();
        try
        {
            await Cmd(store, "logLoad", new { path = wc });
            Assert.Equal(50, svn.LogRequestedLimit);
            Assert.Equal(50, Snap(store).Entries.Count);
            Assert.True(Snap(store).CanMore);
            Assert.Equal("50 revisioni caricate.", Snap(store).Msg);

            await Cmd(store, "logMore");
            Assert.Equal(100, svn.LogRequestedLimit);
            Assert.Equal(100, Snap(store).Entries.Count);
        }
        finally
        {
            Directory.Delete(wc, recursive: true);
        }
    }

    [Fact]
    public async Task Log_QuandoFinisconoLeRevisioni_NonOffreAltroCaricamento()
    {
        var store = new LogStore(new FakeSvnService { LogEntryCount = 10 }, new StubSettings());
        var wc = FakeWorkingCopy();
        try
        {
            await Cmd(store, "logLoad", new { path = wc });

            Assert.False(Snap(store).CanMore);
        }
        finally
        {
            Directory.Delete(wc, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\non\esiste\di\sicuro")]
    [InlineData("")]
    public async Task LogLoad_PercorsoCheNonEUnaWorkingCopy_Rifiutato(string path)
    {
        var svn = new FakeSvnService();
        var store = new LogStore(svn, new StubSettings());

        await Cmd(store, "logLoad", new { path });

        Assert.Equal(0, svn.LogRequestedLimit);
        Assert.Empty(Snap(store).Entries);
        Assert.Equal(path.Length == 0, !Snap(store).Err);
    }

    [Fact]
    public async Task LogLoad_UrlDelRepository_Accettato()
    {
        var svn = new FakeSvnService();
        var store = new LogStore(svn, new StubSettings());

        await Cmd(store, "logLoad", new { path = "https://svn.example.com/repo/trunk" });

        Assert.Equal(50, svn.LogRequestedLimit);
        Assert.Equal("https://svn.example.com/repo/trunk", Snap(store).Path);
    }

    [Fact]
    public async Task AutoDetect_ImpostaIlPercorsoECaricaIlLog()
    {
        var svn = new FakeSvnService();
        var store = new LogStore(svn, new StubSettings());

        await store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);

        Assert.Equal(Wc, Snap(store).Path);
        Assert.NotEmpty(Snap(store).Entries);
    }

    [Fact]
    public async Task AutoDetect_SettingVinceESolutionNonSvn_NonAzzeraIlPercorso()
    {
        var store = new LogStore(new FakeSvnService(), new StubSettings { WorkingCopy = @"C:\config" });
        await store.ApplyAutoDetectedWorkingCopyAsync(@"C:\detected", Ct);
        Assert.Equal(@"C:\config", Snap(store).Path);

        var other = new LogStore(new FakeSvnService(), new StubSettings());
        await other.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);
        await other.ApplyAutoDetectedWorkingCopyAsync(null, Ct);
        Assert.Equal(Wc, Snap(other).Path);
    }

    [Fact]
    public async Task LogSelect_CaricaIlDiff_SoloDiUnaRevisioneInElenco()
    {
        var store = new LogStore(new FakeSvnService { LogEntryCount = 5 }, new StubSettings());
        await store.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);

        await Cmd(store, "logSelect", new { rev = 3 });
        Assert.Equal(3, Snap(store).Sel);
        Assert.Equal("diff", Snap(store).Diff);

        await Assert.ThrowsAsync<ArgumentException>(() => Cmd(store, "logSelect", new { rev = 999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Cmd(store, "logSelect", new { rev = "3" }));
        Assert.Equal(3, Snap(store).Sel);
    }
}
