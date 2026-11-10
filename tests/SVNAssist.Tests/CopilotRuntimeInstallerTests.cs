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

    [Theory]
    [InlineData("github-copilot-1.0.109-win32-x64.tgz")]
    [InlineData("github-copilot-1.0.109-win32-arm64.tgz")]
    public void PinnedHash_CoincideConIlReleaseNoto(string asset)
    {
        var pin = CopilotRuntimeInstaller.PinnedHashes[asset];

        Assert.Equal(pin, CopilotRuntimeInstaller.ResolveExpectedHash(asset, $"{pin}  {asset}\n"));
    }

    [Fact]
    public void PinnedHash_ReleaseConChecksumDiverso_Rifiutato()
    {
        const string asset = "github-copilot-1.0.109-win32-x64.tgz";

        var ex = Assert.Throws<InvalidOperationException>(
            () => CopilotRuntimeInstaller.ResolveExpectedHash(asset, $"{Hash}  {asset}\n"));

        Assert.Contains("non coincide", ex.Message);
    }

    [Fact]
    public void VersioneSenzaPin_RipiegaSulChecksumDelRelease()
        => Assert.Equal(Hash, CopilotRuntimeInstaller.ResolveExpectedHash("github-copilot-9.9.9-win32-x64.tgz", $"{Hash}  github-copilot-9.9.9-win32-x64.tgz\n"));

    [Fact]
    public void ILaPinPerLaVersioneInUsoEsiste()
    {
        var version = CopilotRuntimeInstaller.RuntimeVersion;

        Assert.Contains(CopilotRuntimeInstaller.PinnedHashes.Keys, k => k.Contains($"-{version}-win32-x64"));
        Assert.Contains(CopilotRuntimeInstaller.PinnedHashes.Keys, k => k.Contains($"-{version}-win32-arm64"));
    }

    [Fact]
    public async Task CopyCapped_OltreIlLimite_Errore()
    {
        await using var source = new MemoryStream(new byte[1000]);
        await using var target = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CopilotRuntimeInstaller.CopyCappedAsync(source, target, 500, CancellationToken.None));
    }

    [Fact]
    public async Task CopyCapped_SottoIlLimite_Copia()
    {
        await using var source = new MemoryStream(new byte[300]);
        await using var target = new MemoryStream();

        await CopilotRuntimeInstaller.CopyCappedAsync(source, target, 500, CancellationToken.None);

        Assert.Equal(300, target.Length);
    }

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
