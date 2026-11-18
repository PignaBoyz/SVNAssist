using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>0.6.0: consumo (modello, token, costo della CLI) letto da Claude Code in JSON, dall'API Claude e dal campo usage OpenAI/Gemini; testo unico it/en.</summary>
[Collection("Loc")]
public sealed class AiUsageTests : IDisposable
{
    public AiUsageTests() => Loc.Lang = "it";

    public void Dispose() => Loc.Lang = "it";

    private sealed class Runner(ProcessResult result) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }

    private const string Ok = """{"type":"result","subtype":"success","is_error":false,"result":"feat: login\n","total_cost_usd":0.0412,"usage":{"input_tokens":1000,"cache_creation_input_tokens":100,"cache_read_input_tokens":140,"output_tokens":310},"modelUsage":{"claude-sonnet-5-5":{"outputTokens":300,"costUSD":0.04},"claude-haiku-5-5":{"outputTokens":10}}}""";

    private static CommitStore NewVm() => new(new StubSettings(), new FakeSvnService(), new FakeAiFactory(), new NoopAiPrompt(), () => [], () => "");

    [Fact]
    public async Task ClaudeCode_Json_TestoPulitoEConsumo()
    {
        var runner = new Runner(new ProcessResult(0, Ok, ""));
        var sut = new ClaudeCodeAiService(runner, @"C:\claude.exe", "");

        var text = await sut.GenerateCommitMessageAsync("Index: a.cs\n+x", "italiano", CancellationToken.None);

        Assert.Equal("feat: login", text); // il JSON non arriva mai all'utente
        var args = runner.Arguments.ToList();
        Assert.Equal("json", args[args.IndexOf("--output-format") + 1]);
        Assert.Equal("Sonnet 5.5 · 1.240 token in / 310 out · ≈ 0,04 $", AiUsageText.Format(sut.LastUsage!));
    }

    [Fact]
    public void ClaudeCode_ArrayDiEventi_PrendeIlRisultato()
    {
        var parsed = ClaudeCodeAiService.ParseJsonResult("[{\"type\":\"system\"}," + Ok + "]");
        Assert.Equal("feat: login\n", parsed!.Text);
        Assert.Equal(310, parsed.Usage!.OutputTokens);
    }

    [Theory]
    [InlineData(1, "Invalid API key · Please run /login")]
    [InlineData(0, "Failed to authenticate. API Error: 401 token expired")]
    public async Task ClaudeCode_ErroreNelJson_RestaClassificatoComeAccessoScaduto(int exit, string result)
    {
        var json = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"" + result + "\"}";
        var sut = new ClaudeCodeAiService(new Runner(new ProcessResult(exit, json, "")), @"C:\claude.exe", "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GenerateCommitMessageAsync("d", "auto", CancellationToken.None));

        Assert.Equal(AiErrorKind.Auth, AiErrors.Classify(ex.Message));
        var vm = NewVm();
        vm.SetAiError("Claude Code: " + ex.Message);
        Assert.Equal("wizardOpenAuth", vm.GetSnapshot(null).AiAction!.Cmd);
        Assert.Equal("claudecode", vm.FailedProvider);
    }

    [Fact]
    public async Task ClaudeCode_ModelloRitiratoNelJson_ProponeAggiornaElencoModelli()
    {
        const string json = """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"There's an issue with the selected model (claude-old-1). It may not exist or you may not have access to it."}""";
        var sut = new ClaudeCodeAiService(new Runner(new ProcessResult(1, json, "")), @"C:\claude.exe", "claude-old-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GenerateCommitMessageAsync("d", "auto", CancellationToken.None));

        Assert.Equal(AiErrorKind.ModelUnavailable, AiErrors.Classify(ex.Message));
        var vm = NewVm();
        vm.SetAiError("Claude Code: " + ex.Message);
        Assert.Equal("wizardOpenModels", vm.GetSnapshot(null).AiAction!.Cmd);
    }

    [Fact]
    public async Task ClaudeCode_StdoutNonJson_RestaTestoSenzaConsumo()
    {
        var sut = new ClaudeCodeAiService(new Runner(new ProcessResult(0, "feat: x\n", "")), @"C:\claude.exe", "");
        Assert.Equal("feat: x", await sut.GenerateCommitMessageAsync("d", "auto", CancellationToken.None));
        Assert.Null(sut.LastUsage);
    }

    [Fact]
    public void OpenAi_UsageDallaRisposta_SoloToken()
    {
        var u = AiService.ReadUsage("""{"model":"models/gemini-2.5-flash","choices":[],"usage":{"prompt_tokens":1240,"completion_tokens":310,"total_tokens":1550}}""");
        Assert.Equal(new AiUsage("gemini-2.5-flash", 1240, 310), u);
        Assert.Equal("gemini-2.5-flash · 1.240 token in / 310 out", AiUsageText.Format(u!)); // mai un costo
        Assert.Null(AiService.ReadUsage("""{"choices":[]}"""));
    }

    [Fact]
    public void Formato_Inglese_NumeriEPrezzoInLingua()
    {
        Loc.Lang = "en";
        Assert.Equal("Sonnet 5.5 · 1,240 tokens in / 310 out · ≈ $0.04", AiUsageText.Format(new AiUsage("claude-sonnet-5-5", 1240, 310, 0.0412m)));
        Assert.Equal("claude-x", AiUsageText.ShortModel("models/claude-x"));
        Assert.Equal("Opus 4.1", AiUsageText.ShortModel("claude-opus-4-1-20250805"));
    }

    [Fact]
    public async Task Opzione_MostraUnaRigaSolaConIlConsumo()
    {
        var ai = new FakeAiFactory { Custom = new UsageAi() };
        var path = Path.Combine(Path.GetTempPath(), "a.cs");
        var vm = new CommitStore(new StubSettings { ShowUsage = true }, new FakeSvnService(), ai, new NoopAiPrompt(), () => [path], () => Path.GetTempPath());
        await vm.GenerateAsync(CancellationToken.None);
        Assert.Contains("Sonnet 5.5 · 10 token in / 5 out", vm.AiStatus);
        Assert.DoesNotContain("costo", vm.AiStatus);
    }

    private sealed class UsageAi : IAiService, IReportsModel
    {
        public string? LastModelUsed => "claude-sonnet-5-5";
        public AiUsage? LastUsage => new("claude-sonnet-5-5", 10, 5);
        public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null) => Task.FromResult("Titolo");
        public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null, string? style = null) => Task.FromResult("Titolo");
    }

    [Fact]
    public void Gemini_ElencoModelli_ToglieIlPrefissoModels()
    {
        var list = AiModelCatalog.ParseOpenAiModels("""{"data":[{"id":"models/gemini-2.5-pro"},{"id":"models/gemini-2.5-flash"},{"id":"models/text-embedding-004"}]}""");
        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro"], list.Select(m => m.Id));
        Assert.Equal("gemini-2.5-pro", AiServiceFactory.NormalizeGeminiModel("models/gemini-2.5-pro"));
    }
}
