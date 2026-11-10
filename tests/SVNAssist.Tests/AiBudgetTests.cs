using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Limiti di dimensione per provider (D16): il diff intero viaggia solo se sta nel budget.</summary>
public class AiBudgetTests
{
    private sealed class RecordingAi(int? budget) : AiServiceBase
    {
        public List<string> UserMessages { get; } = [];

        protected override int MaxUserMessageChars => budget ?? base.MaxUserMessageChars;

        protected override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            UserMessages.Add(userMessage);
            return Task.FromResult("ok");
        }
    }

    // Più file da ~15 KB: ciascuno sotto il tetto per file di DiffLimiter, così il diff arriva intero alla logica di budget.
    private static string BigDiff(int lines)
        => string.Join(
            "\n",
            Enumerable.Range(0, (lines + 499) / 500).Select(f =>
                $"Index: f{f}.cs\n" + string.Join(
                    "\n", Enumerable.Range(f * 500, Math.Min(500, lines - f * 500)).Select(i => $"+riga numero {i:D5} del diff"))));

    [Fact]
    public async Task BudgetDefault_DiffGrande_VieneRiassuntoABlocchi()
    {
        var sut = new RecordingAi(budget: null);

        await sut.GenerateCommitMessageAsync(BigDiff(3000), "auto", CancellationToken.None);

        Assert.True(sut.UserMessages.Count > 1);
    }

    [Fact]
    public async Task BudgetAmpio_StessoDiff_ViaggiaIntero()
    {
        var diff = BigDiff(3000);
        var sut = new RecordingAi(budget: 100_000);

        await sut.GenerateCommitMessageAsync(diff, "auto", CancellationToken.None);

        var single = Assert.Single(sut.UserMessages);
        Assert.Contains("+riga numero 02999 del diff", single);
    }
}
