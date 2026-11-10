using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Il timeout vale per le operazioni rapide; update e commit (lunghi, avviati dall'utente) non ne hanno.
/// </summary>
public class SvnServiceTimeoutTests
{
    private static readonly TimeSpan TinyTimeout = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan SlowOperation = TimeSpan.FromMilliseconds(400);

    /// <summary>Runner lento: completa dopo <see cref="SlowOperation"/> solo se non viene annullato prima.</summary>
    private sealed class SlowRunner(string output) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            await Task.Delay(SlowOperation, ct);
            return new ProcessResult(0, output, string.Empty);
        }
    }

    private static SvnService Create(string output = "") => new(@"C:\fake\svn.exe", new SlowRunner(output), TinyTimeout);

    [Fact]
    public async Task UpdateAsync_OltreIlTimeout_NonVieneInterrotto()
    {
        var result = await Create("Updated to revision 7.\n").UpdateAsync(@"C:\wc", CancellationToken.None);

        Assert.Equal(7, result.Revision);
    }

    [Fact]
    public async Task CommitAsync_OltreIlTimeout_NonVieneInterrotto()
    {
        var revision = await Create("Committed revision 9.\n").CommitAsync([@"C:\wc\a.txt"], "msg", CancellationToken.None);

        Assert.Equal(9, revision);
    }

    [Fact]
    public async Task OperazioneRapida_OltreIlTimeout_VieneInterrotta()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().GetStatusAsync(@"C:\wc", CancellationToken.None));

        Assert.Contains("non ha risposto", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_AnnullatoDallUtente_Annulla()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().UpdateAsync(@"C:\wc", cts.Token));
    }
}
