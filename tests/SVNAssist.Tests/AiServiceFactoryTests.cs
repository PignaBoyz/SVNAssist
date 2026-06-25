using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Unit test per <see cref="AiServiceFactory"/>, abilitati dall'estrazione di
/// <see cref="ISvnAssistSettings"/>: ora possiamo iniettare un settings finto
/// senza dipendere dall'observer generato da Visual Studio.
/// </summary>
public class AiServiceFactoryTests
{
    /// <summary>Settings finto con valori configurabili.</summary>
    private sealed class FakeSettings : ISvnAssistSettings
    {
        public string Endpoint { get; init; } = "https://api.test.com/v1/chat/completions";
        public string ApiKey { get; init; } = string.Empty;
        public string Model { get; init; } = "gpt-4o-mini";
        public string Language { get; init; } = "auto";
        public string WorkingCopyPath { get; init; } = string.Empty;

        public Task<string> GetAiEndpointAsync(CancellationToken ct) => Task.FromResult(Endpoint);
        public Task<string> GetAiApiKeyAsync(CancellationToken ct) => Task.FromResult(ApiKey);
        public Task<string> GetAiModelAsync(CancellationToken ct) => Task.FromResult(Model);
        public Task<string> GetDefaultLanguageAsync(CancellationToken ct) => Task.FromResult(Language);
        public Task<string> GetWorkingCopyPathAsync(CancellationToken ct) => Task.FromResult(WorkingCopyPath);
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

    [Fact]
    public async Task CreateAsync_NessunaApiKeyNessunToken_RestituisceNull()
    {
        var factory = new AiServiceFactory(new FakeSettings { ApiKey = "" }, new FakeTokenResolver(null));

        var result = await factory.CreateAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_ApiKeyEsplicita_VinceSulResolver()
    {
        var resolver = new FakeTokenResolver("token-dal-resolver");
        var factory = new AiServiceFactory(new FakeSettings { ApiKey = "ghp_esplicita" }, resolver);

        var result = await factory.CreateAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<AiService>(result);
        // Con una key esplicita non interroghiamo il resolver automatico.
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task CreateAsync_SenzaApiKeyMaConTokenAutomatico_RestituisceServizio()
    {
        var factory = new AiServiceFactory(new FakeSettings { ApiKey = "" }, new FakeTokenResolver("ghp_auto"));

        var result = await factory.CreateAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<AiService>(result);
    }
}
