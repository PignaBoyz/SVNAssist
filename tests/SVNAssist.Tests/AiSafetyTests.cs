using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Tetto ai dati inviati all'AI, timeout, endpoint sicuro, trasparenza e file sensibili.</summary>
public class AiSafetyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Section(string name, int chars) => $"Index: {name}\n" + new string('+', chars) + "\n";

    // ── DiffLimiter ──────────────────────────────────────────────────

    [Fact]
    public void Limit_DiffPiccolo_NonCambia()
    {
        var result = DiffLimiter.Limit("Index: a\n+x\n");

        Assert.False(result.WasTruncated);
        Assert.Equal("Index: a\n+x\n", result.Diff);
    }

    [Fact]
    public void Limit_UnFileEnorme_TroncatoPerFileConMarcatore_AltriFileIntatti()
    {
        var diff = Section("grande.bin", 500_000) + Section("piccolo.cs", 100);

        var result = DiffLimiter.Limit(diff);

        Assert.True(result.WasTruncated);
        Assert.Contains(DiffLimiter.TruncatedMarker, result.Diff);
        Assert.Contains("Index: piccolo.cs", result.Diff);
        Assert.True(result.Diff.Length < DiffLimiter.MaxFileChars + 1000);
    }

    [Fact]
    public void Limit_MoltiFile_TotaleLimitatoEAltriFileOmessi()
    {
        var diff = string.Concat(Enumerable.Range(0, 100).Select(i => Section($"f{i}.cs", 15_000)));

        var result = DiffLimiter.Limit(diff);

        Assert.True(result.WasTruncated);
        Assert.True(result.OmittedFiles > 0);
        Assert.True(result.Diff.Length <= DiffLimiter.MaxTotalChars + 500);
        Assert.Contains("file omessi", result.Diff);
    }

    private sealed class CountingAi : AiServiceBase
    {
        public int Calls { get; private set; }
        public int TotalChars { get; private set; }

        protected override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            Calls++;
            TotalChars += userMessage.Length;
            return Task.FromResult("ok");
        }
    }

    [Fact]
    public async Task FileAggiuntoEnorme_NonGeneraMigliaiaDiRichieste()
    {
        // svn diff stampa per intero i file aggiunti: 50 MB di "+" non devono diventare migliaia di richieste.
        var diff = Section("enorme.sql", 50_000_000);
        var ai = new CountingAi();

        await ai.GenerateCommitMessageAsync(diff, "auto", Ct);

        Assert.True(ai.Calls <= 12, $"troppe richieste: {ai.Calls}");
        Assert.True(ai.TotalChars < 200_000);
    }

    // ── Timeout della singola richiesta ──────────────────────────────

    private sealed class HangingAi : AiServiceBase
    {
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(50);

        internal override TimeSpan CallTimeout => Timeout;

        protected override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            return string.Empty;
        }
    }

    [Fact]
    public async Task ProviderAppeso_TimeoutConMessaggioDistinto()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => new HangingAi().GenerateCommitMessageAsync("Index: a\n+x\n", "auto", Ct));

        Assert.Contains("non ha risposto", ex.Message);
    }

    [Fact]
    public async Task ProviderAppeso_AnnulloDallUtente_RestaOperationCanceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        var ai = new HangingAi { Timeout = TimeSpan.FromMinutes(5) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ai.GenerateCommitMessageAsync("Index: a\n+x\n", "auto", cts.Token));
    }

    // ── Endpoint: chiave solo su https (o loopback) ──────────────────

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions", "k", true)]
    [InlineData("http://localhost:11434/v1/chat/completions", "k", true)]
    [InlineData("http://127.0.0.1:1234/v1", "k", true)]
    [InlineData("http://example.com/v1", "", true)] // senza chiave non c'è nulla da proteggere
    [InlineData("http://example.com/v1", "k", false)]
    [InlineData("http://10.0.0.5/v1", "k", false)]
    [InlineData("ftp://example.com/v1", "k", false)]
    [InlineData("non-un-url", "k", false)]
    public void EnsureSafeEndpoint(string endpoint, string key, bool allowed)
    {
        if (allowed)
        {
            AiService.EnsureSafeEndpoint(endpoint, key);
            return;
        }

        Assert.Throws<InvalidOperationException>(() => AiService.EnsureSafeEndpoint(endpoint, key));
    }

    // ── File sensibili ───────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\wc\.env", true)]
    [InlineData(@"C:\wc\.env.production", true)]
    [InlineData(@"C:\wc\certs\server.pfx", true)]
    [InlineData(@"C:\wc\server.PEM", true)]
    [InlineData(@"C:\wc\private.key", true)]
    [InlineData(@"C:\wc\my-secrets.json", true)]
    [InlineData(@"C:\wc\appsettings.Production.json", true)]
    [InlineData(@"C:\wc\appsettings.json", false)]
    [InlineData(@"C:\wc\Program.cs", false)]
    [InlineData(@"C:\wc\environment.cs", false)]
    public void LooksSensitive(string path, bool expected) => Assert.Equal(expected, SensitiveFiles.LooksSensitive(path));

    // ── Trasparenza nel ViewModel ────────────────────────────────────

    private const string Wc = @"C:\wc";

    private sealed class Fixture
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeAiFactory Ai { get; } = new();
        public StubSettings Settings { get; } = new();
        public ChangesStore Vm { get; }

        public Fixture(params (string Path, SvnStatusKind Status)[] items)
        {
            foreach (var (path, status) in items)
                Svn.Statuses.Add(new SvnFileStatus(path, status, IsTreeConflict: false));
            Vm = new ChangesStore(
                Settings, Svn, Ai, new NoopAiPrompt(), Dialogs, new FakeHost(),
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())))
            {
                WorkingCopyPath = Wc,
            };
        }
    }

    [Fact]
    public async Task PrimoUsoAi_ChiedeConfermaUnaSolaVolta_ElencandoIlProvider()
    {
        var f = new Fixture(("a.cs", SvnStatusKind.Modified));
        await f.Vm.RefreshAsync(Ct);

        await f.Vm.CommitMessage.GenerateAsync(Ct);
        await f.Vm.CommitMessage.GenerateAsync(Ct);

        var message = Assert.Single(f.Dialogs.Messages);
        Assert.Contains("dei file da committare", message);
        Assert.Equal(2, f.Ai.Service.Languages.Count);
    }

    [Fact]
    public async Task ConfermaRifiutata_NienteAiNienteDiff()
    {
        var f = new Fixture(("a.cs", SvnStatusKind.Modified));
        await f.Vm.RefreshAsync(Ct);
        f.Dialogs.ConfirmResult = false;

        await f.Vm.CommitMessage.GenerateAsync(Ct);

        Assert.Empty(f.Ai.Service.Languages);
        Assert.Empty(f.Svn.AiDiffRequests);
        Assert.Contains("nessun dato", f.Vm.CommitMessage.AiStatus);
    }

    [Fact]
    public async Task FileSospetto_AvvisoNellaConferma_EUnaVoltaSola()
    {
        var f = new Fixture(("appsettings.Production.json", SvnStatusKind.Modified), ("a.cs", SvnStatusKind.Modified));
        await f.Vm.RefreshAsync(Ct);

        await f.Vm.CommitMessage.GenerateAsync(Ct);
        await f.Vm.CommitMessage.GenerateAsync(Ct);

        var message = Assert.Single(f.Dialogs.Messages);
        Assert.Contains("segreti", message);
        Assert.Contains("appsettings.Production.json", message);
    }

    [Fact]
    public async Task DiffEnorme_VieneTroncatoEL_UtenteLoVede()
    {
        var f = new Fixture(("a.cs", SvnStatusKind.Modified));
        await f.Vm.RefreshAsync(Ct);
        f.Svn.AiDiff = Section("a.cs", 400_000);

        await f.Vm.CommitMessage.GenerateAsync(Ct);

        Assert.True(f.Ai.Service.Diffs[0].Length < 25_000);
        Assert.Contains("diff molto grande", f.Vm.CommitMessage.AiStatus);
    }

    [Fact]
    public void DescribeChain_ElencaITuttiIProvider()
    {
        var chain = new FallbackAiService([("Claude Code", new RecordingAi()), ("GitHub Copilot", new RecordingAi())]);

        Assert.Equal("Claude Code → GitHub Copilot", CommitStore.DescribeChain(chain));
    }
}
