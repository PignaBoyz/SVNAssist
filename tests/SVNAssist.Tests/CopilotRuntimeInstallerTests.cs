using System.Net.Http;
using GitHub.Copilot;
using SVNAssist.Services;

namespace SVNAssist.Tests;

public class CopilotRuntimeInstallerTests
{
    private const string Asset = "github-copilot-1.0.109-win32-x64.tgz";
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void ParseChecksum_TrovaLaRigaDellAsset()
    {
        var sums = $"{new string('b', 64)}  copilot-win32-x64.zip\n{Hash}  {Asset}\n";

        Assert.Equal(Hash, CopilotRuntimeInstaller.ParseChecksum(sums, Asset));
    }

    [Fact]
    public void ParseChecksum_AssetAssente_Errore()
        => Assert.Throws<InvalidOperationException>(
            () => CopilotRuntimeInstaller.ParseChecksum($"{Hash}  altro.tgz\n", Asset));

    [Fact]
    public void RuntimeVersion_LettaDallAssembly()
        => Assert.Matches(@"^\d+\.\d+\.\d+", CopilotRuntimeInstaller.RuntimeVersion);

    /// <summary>
    /// End-to-end reale (scarica ~100 MB): gira solo con SVNASSIST_COPILOT_E2E=1.
    /// Verifica download, checksum, estrazione e avvio del runtime.
    /// </summary>
    [Fact]
    public async Task EnsureInstalled_ScaricaEAvviaIlRuntime()
    {
        if (Environment.GetEnvironmentVariable("SVNASSIST_COPILOT_E2E") != "1")
            return;

        using var http = new HttpClient();
        var path = await CopilotRuntimeInstaller.EnsureInstalledAsync(http, CancellationToken.None);
        Assert.True(File.Exists(path));

        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(path),
            UseLoggedInUser = false,
        });
        await client.StartAsync();
        await client.PingAsync();
    }
}
