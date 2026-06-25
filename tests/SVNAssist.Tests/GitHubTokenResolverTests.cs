using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Unit test per <see cref="GitHubTokenResolver"/>. La scoperta di <c>gh</c>, l'esecuzione
/// del processo e la lettura delle variabili d'ambiente sono iniettate, quindi i test
/// non dipendono da GitHub CLI installato né dall'ambiente reale.
/// </summary>
public class GitHubTokenResolverTests
{
    /// <summary>Runner finto che restituisce un risultato fisso (o lancia, per simulare gh assente).</summary>
    private sealed class StubProcessRunner : IProcessRunner
    {
        public ProcessResult Result { get; init; } = new(0, string.Empty, string.Empty);
        public bool Throws { get; init; }

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        {
            if (Throws)
                throw new InvalidOperationException("gh non avviabile");
            return Task.FromResult(Result);
        }
    }

    private static Func<string, string?> NoEnv => _ => null;
    private static Func<string?> GhFound => () => @"C:\fake\gh.exe";
    private static Func<string?> GhMissing => () => null;

    [Fact]
    public async Task ResolveAsync_GhRestituisceToken_RestituisceTokenTrimmato()
    {
        var runner = new StubProcessRunner { Result = new ProcessResult(0, "ghp_fromgh\n", string.Empty) };
        var sut = new GitHubTokenResolver(runner, NoEnv, GhFound);

        var token = await sut.ResolveAsync(CancellationToken.None);

        Assert.Equal("ghp_fromgh", token);
    }

    [Fact]
    public async Task ResolveAsync_GhNonLoggato_RipiegaSuVariabileAmbiente()
    {
        // gh esce con codice != 0 (non loggato) → si usa GITHUB_TOKEN
        var runner = new StubProcessRunner { Result = new ProcessResult(1, string.Empty, "not logged in") };
        Func<string, string?> env = name => name == "GITHUB_TOKEN" ? "ghp_fromenv" : null;
        var sut = new GitHubTokenResolver(runner, env, GhFound);

        var token = await sut.ResolveAsync(CancellationToken.None);

        Assert.Equal("ghp_fromenv", token);
    }

    [Fact]
    public async Task ResolveAsync_GhAssenteEnvVuoto_RestituisceNull()
    {
        var runner = new StubProcessRunner { Throws = true };
        var sut = new GitHubTokenResolver(runner, NoEnv, GhMissing);

        var token = await sut.ResolveAsync(CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task ResolveAsync_LeggeGhTokenComando()
    {
        // Verifica che venga invocato proprio "gh auth token"
        IReadOnlyList<string>? capturedArgs = null;
        var runner = new CapturingRunner(args => capturedArgs = args);
        var sut = new GitHubTokenResolver(runner, NoEnv, GhFound);

        await sut.ResolveAsync(CancellationToken.None);

        Assert.Equal(["auth", "token"], capturedArgs);
    }

    private sealed class CapturingRunner(Action<IReadOnlyList<string>> capture) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        {
            capture(arguments);
            return Task.FromResult(new ProcessResult(0, "ghp_x", string.Empty));
        }
    }
}
