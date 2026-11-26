using System.Text.Json;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Tema della sola finestra Log: letto dalle impostazioni dell'host all'apertura, salvato con setLogTheme.</summary>
public class LogThemeTests
{
    [Fact]
    public async Task SetLogTheme_SalvaNellHost_ELoRiportaNelloSnapshot()
    {
        var host = new FakeHost();
        var log = new LogStore(new FakeSvnService(), new StubSettings(), host);

        await log.DispatchAsync("setLogTheme", JsonSerializer.SerializeToElement(new { mode = "light" }), CancellationToken.None);

        Assert.Contains(("logTheme", "light"), host.SettingWrites);
        Assert.Equal("light", ((LogState)log.GetSnapshot()).LogTheme);
        await Assert.ThrowsAsync<ArgumentException>(() => log.DispatchAsync("setLogTheme", JsonSerializer.SerializeToElement(new { mode = "rosa" }), CancellationToken.None));
    }

    [Fact]
    public async Task AllApertura_LeggeIlTemaDalleImpostazioni()
    {
        var host = new FakeHost();
        host.Settings["logTheme"] = "dark";
        var log = new LogStore(new FakeSvnService(), new StubSettings(), host);

        await log.OnReadyAsync(CancellationToken.None);

        Assert.Equal("dark", ((LogState)log.GetSnapshot()).LogTheme);
    }
}
