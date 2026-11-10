using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Test per la catena di fallback, il provider Claude Code e il parsing dei diff.</summary>
public class AiProvidersTests
{
    private sealed class StubAi(Func<string> answer) : IAiService
    {
        public int Calls { get; private set; }

        public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer());
        }

        public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct)
            => GenerateCommitMessageAsync(diff, language, ct);
    }

    private sealed class CapturingRunner(ProcessResult result) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string? StandardInput { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Arguments = arguments;
            StandardInput = standardInput;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Fallback_PrimoProviderFallisce_UsaIlSecondo()
    {
        var broken = new StubAi(() => throw new InvalidOperationException("niente abbonamento"));
        var working = new StubAi(() => "fix: corretto bug");
        var sut = new FallbackAiService([("Copilot", broken), ("Claude", working)]);

        var message = await sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None);

        Assert.Equal("fix: corretto bug", message);
        Assert.Equal(1, broken.Calls);
    }

    [Fact]
    public async Task Fallback_TuttiFalliscono_ErroreConTuttiIMotivi()
    {
        var sut = new FallbackAiService([
            ("Copilot", new StubAi(() => throw new InvalidOperationException("A"))),
            ("Claude", new StubAi(() => throw new InvalidOperationException("B"))),
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None));

        Assert.Contains("Copilot: A", ex.Message);
        Assert.Contains("Claude: B", ex.Message);
    }

    [Fact]
    public async Task ClaudeCode_PassaIlDiffSuStdinSenzaStrumenti()
    {
        var runner = new CapturingRunner(new ProcessResult(0, "feat: aggiunto login\n", string.Empty));
        var sut = new ClaudeCodeAiService(runner, @"C:\claude.exe", "claude-opus-5-5");

        var message = await sut.GenerateCommitMessageAsync("Index: a.cs\n+riga", "italiano", CancellationToken.None);

        Assert.Equal("feat: aggiunto login", message);
        Assert.Contains("+riga", runner.StandardInput);
        // "--tools" seguito da "" = nessuno strumento disponibile.
        var toolsIndex = runner.Arguments.ToList().IndexOf("--tools");
        Assert.Equal(string.Empty, runner.Arguments[toolsIndex + 1]);
        Assert.Contains("claude-opus-5-5", runner.Arguments);
    }

    [Fact]
    public async Task ClaudeCode_ExitCodeDiversoDaZero_LanciaErrore()
    {
        var runner = new CapturingRunner(new ProcessResult(1, string.Empty, "Failed to authenticate"));
        var sut = new ClaudeCodeAiService(runner, @"C:\claude.exe", "claude-opus-5-5");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateCommitMessageAsync("diff", "auto", CancellationToken.None));

        Assert.Contains("Failed to authenticate", ex.Message);
    }

    [Fact]
    public void ExtractFilePaths_FormatoSvnConTab_NessunDoppione()
    {
        var diff = "Index: src/a.cs\n" +
                   "===================================================================\n" +
                   "--- src/a.cs\t(revision 5)\n" +
                   "+++ src/a.cs\t(working copy)\n";

        Assert.Equal(["src/a.cs"], AiServiceBase.ExtractFilePaths(diff));
    }
}
