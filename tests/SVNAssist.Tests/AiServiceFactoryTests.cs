using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Unit test per <see cref="AiServiceFactory"/>: scelta del provider, catena Auto
/// e protezione del token GitHub.
/// </summary>
public class AiServiceFactoryTests
{
    /// <summary>Settings finto con valori configurabili.</summary>
    private sealed class FakeSettings : ISvnAssistSettings
    {
        public string Provider { get; init; } = "auto";
        public string Endpoint { get; init; } = "https://api.test.com/v1/chat/completions";
        public string ApiKey { get; init; } = string.Empty;
        public string AnthropicKey { get; init; } = string.Empty;
        public string Model { get; init; } = "openai/gpt-4o-mini";
        public string Language { get; init; } = "auto";
        public string WorkingCopyPath { get; init; } = string.Empty;

        public Task<string> GetAiProviderAsync(CancellationToken ct) => Task.FromResult(Provider);
        public Task<string> GetClaudeModelAsync(CancellationToken ct) => Task.FromResult("claude-sonnet-5-5");
        public Task<string> GetAnthropicApiKeyAsync(CancellationToken ct) => Task.FromResult(AnthropicKey);
        public Task<string> GetCopilotModelAsync(CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<string> GetAiEndpointAsync(CancellationToken ct) => Task.FromResult(Endpoint);
        public Task<string> GetAiApiKeyAsync(CancellationToken ct) => Task.FromResult(ApiKey);
        public Task<string> GetAiModelAsync(CancellationToken ct) => Task.FromResult(Model);
        public Task<string> GetDefaultLanguageAsync(CancellationToken ct) => Task.FromResult(Language);
        public Task<string> GetWorkingCopyPathAsync(CancellationToken ct) => Task.FromResult(WorkingCopyPath);
        public Task<string> GetWindowThemeAsync(CancellationToken ct) => Task.FromResult("auto");
        public Task SetWindowThemeAsync(string mode, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Resolver finto: restituisce un token preimpostato (o null).</summary>
    private sealed class FakeTokenResolver(string? token) : IGitHubTokenResolver
    {
        public int CallCount { get; private set; }

        public Task<string?> ResolveAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(token);
        }
    }

    private const string GitHubModels = "https://models.github.ai/inference/chat/completions";

    /// <summary>Factory senza Claude Code installato e senza variabili d'ambiente.</summary>
    private static AiServiceFactory Create(FakeSettings settings, IGitHubTokenResolver resolver, string? claudeExe = null)
        => new(settings, resolver, processRunner: null, claudeLocator: () => claudeExe, environmentReader: _ => null,
            copilotRuntimeInstalled: () => true);

    [Fact]
    public async Task CreateAsync_NessunProviderDisponibile_RestituisceNull()
    {
        var result = await Create(new FakeSettings(), new FakeTokenResolver(null)).CreateAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_OpenAi_EndpointVuoto_NonConfigurato_RestituisceNull()
    {
        var factory = Create(new FakeSettings { Provider = "auto", Endpoint = "  ", ApiKey = "sk-x" }, new FakeTokenResolver(null));

        Assert.Null(await factory.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_OpenAi_ApiKeyEsplicita_VinceSulResolver()
    {
        var resolver = new FakeTokenResolver("token-dal-resolver");
        var factory = Create(new FakeSettings { Provider = "openai", ApiKey = "sk-esplicita" }, resolver);

        var result = await factory.CreateAsync(CancellationToken.None);

        Assert.IsType<AiService>(result);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task CreateAsync_OpenAi_GitHubModelsConTokenAutomatico_RestituisceServizio()
    {
        var factory = Create(new FakeSettings { Provider = "openai", Endpoint = GitHubModels }, new FakeTokenResolver("ghp_auto"));

        Assert.IsType<AiService>(await factory.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_OpenAi_EndpointDiTerzi_NonRiceveIlTokenGitHub()
    {
        // Il token GitHub non deve mai partire verso endpoint diversi da GitHub Models.
        var factory = Create(new FakeSettings { Provider = "openai" }, new FakeTokenResolver("ghp_segreto"));

        Assert.Null(await factory.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_Auto_ConTokenEClaude_ProvaCopilotPoiClaudeCode()
    {
        var factory = Create(new FakeSettings(), new FakeTokenResolver("ghp_auto"), claudeExe: @"C:\claude.exe");

        var result = Assert.IsType<FallbackAiService>(await factory.CreateAsync(CancellationToken.None));

        Assert.Equal(["GitHub Copilot", "Claude Code"], result.ProviderNames.Take(2));
    }

    [Fact]
    public async Task CreateAsync_Auto_SoloClaudeCode_RestituisceClaudeCode()
    {
        var factory = Create(new FakeSettings(), new FakeTokenResolver(null), claudeExe: @"C:\claude.exe");

        Assert.IsType<ClaudeCodeAiService>(await factory.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_Claude_ConApiKey_RestituisceClaudeApi()
    {
        var factory = Create(new FakeSettings { Provider = "claude", AnthropicKey = "sk-ant-test" }, new FakeTokenResolver("ghp_x"));

        Assert.IsType<AnthropicAiService>(await factory.CreateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("https://models.github.ai/inference/chat/completions", true)]
    [InlineData("https://models.github.ai.evil.com/chat/completions", false)]
    [InlineData("http://models.github.ai/inference/chat/completions", false)]
    [InlineData("https://api.openai.com/v1/chat/completions", false)]
    public void IsGitHubModelsEndpoint_SoloHostUfficialeHttps(string endpoint, bool expected)
        => Assert.Equal(expected, AiServiceFactory.IsGitHubModelsEndpoint(endpoint));

    [Fact]
    public async Task CreateAsync_Auto_RuntimeCopilotNonScaricato_NonAvviaDownload()
    {
        var factory = new AiServiceFactory(new FakeSettings(), new FakeTokenResolver("ghp_auto"),
            claudeLocator: () => null, environmentReader: _ => null, copilotRuntimeInstalled: () => false);

        // Solo token GitHub + endpoint di terzi: nessun provider, e soprattutto nessun download a sorpresa.
        Assert.Null(await factory.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_CopilotEsplicito_RuntimeNonScaricato_UsaCopilot()
    {
        var factory = new AiServiceFactory(new FakeSettings { Provider = "copilot" }, new FakeTokenResolver("ghp_x"),
            claudeLocator: () => null, environmentReader: _ => null, copilotRuntimeInstalled: () => false);

        Assert.IsType<CopilotAiService>(await factory.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_CopilotEsplicito_SenzaToken_NonUtilizzabile()
    {
        // Coerente col checklist: senza login GitHub niente Copilot, quindi parte la guida al login.
        var factory = new AiServiceFactory(new FakeSettings { Provider = "copilot" }, new FakeTokenResolver(null),
            claudeLocator: () => null, environmentReader: _ => null, copilotRuntimeInstalled: () => false);

        Assert.Null(await factory.CreateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("claude-haiku-5-5", "claude-haiku-5-5")]
    [InlineData("claude-opus-4", "claude-sonnet-5-5")]
    [InlineData("", "claude-sonnet-5-5")]
    public void ResolveClaudeModel_FuoriListaRicadeSulDefault(string saved, string expected)
        => Assert.Equal(expected, AiServiceFactory.ResolveClaudeModel(saved));

    [Fact]
    public void NormalizeCopilotModel_FuoriLista_DiventaDefault()
        => Assert.Equal(string.Empty, AiServiceFactory.NormalizeCopilotModel("modello-vecchio"));

    [Theory]
    [InlineData(GitHubModels, "gpt-4o-mini", "openai/gpt-4o-mini")]
    [InlineData(GitHubModels, "openai/gpt-4o", "openai/gpt-4o")]
    [InlineData(GitHubModels, "microsoft/Phi-3-medium-128k-instruct", "openai/gpt-4o-mini")]
    [InlineData("https://api.openai.com/v1/chat/completions", "openai/gpt-4o-mini", "gpt-4o-mini")]
    [InlineData("http://localhost:11434/v1/chat/completions", "llama3", "llama3")]
    [InlineData(GitHubModels, "", "openai/gpt-4o-mini")]
    public void NormalizeOpenAiModel_AdattaLIdALEndpoint(string endpoint, string model, string expected)
        => Assert.Equal(expected, AiServiceFactory.NormalizeOpenAiModel(endpoint, model));

    [Theory]
    [InlineData("default", "")]
    [InlineData("", "")]
    [InlineData("gpt-4.1", "gpt-4.1")]
    public void NormalizeCopilotModel_DefaultDiventaVuoto(string model, string expected)
        => Assert.Equal(expected, AiServiceFactory.NormalizeCopilotModel(model));

    [Fact]
    public void ModelloClaudeDiDefault_ESonnet()
        => Assert.Equal("claude-sonnet-5-5", SettingDefinitions.DefaultClaudeModel);
}
