using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Unit test per <see cref="SvnService"/> che verificano la <b>costruzione degli argomenti</b>
/// passati a svn.exe, usando un <see cref="IProcessRunner"/> finto.
/// </summary>
/// <remarks>
/// A differenza di <see cref="SvnServiceTests"/> (test di integrazione che richiedono SVN
/// installato), questi sono unit test puri: non spawnano alcun processo e girano ovunque,
/// anche in CI senza Subversion. Verificano che SvnService traduca correttamente le operazioni
/// in argomenti svn, inclusi i casi insidiosi (messaggi con spazi/virgolette, liste vuote).
/// </remarks>
public class SvnServiceArgsTests
{
    /// <summary>Runner finto che cattura l'ultima invocazione e restituisce un risultato fisso.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = [];
        public string? LastWorkingDirectory { get; private set; }
        public ProcessResult Result { get; set; } = new(0, string.Empty, string.Empty);

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        {
            CallCount++;
            LastFileName = fileName;
            LastArguments = arguments;
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(Result);
        }
    }

    // Path fittizio: passandolo esplicitamente evitiamo la ricerca di un svn.exe reale.
    private const string FakeSvnPath = @"C:\fake\svn.exe";

    private static SvnService CreateSut(FakeProcessRunner runner) => new(FakeSvnPath, runner);

    [Fact]
    public async Task CommitAsync_MessaggioConSpaziEVirgolette_PassatoComeSingoloArgomento()
    {
        // Arrange
        var runner = new FakeProcessRunner();
        var sut = CreateSut(runner);
        var message = "fix: corretto \"bug\" con spazi";

        // Act
        await sut.CommitAsync([@"C:\wc\a.cs", @"C:\wc\file con spazi.cs"], message, CancellationToken.None);

        // Assert — niente escaping manuale: il messaggio è un unico argomento intatto
        Assert.Equal(FakeSvnPath, runner.LastFileName);
        Assert.Equal(
            ["commit", "-m", message, @"C:\wc\a.cs", @"C:\wc\file con spazi.cs"],
            runner.LastArguments);
    }

    [Fact]
    public async Task CommitAsync_NessunPath_NonInvocaSvn()
    {
        var runner = new FakeProcessRunner();
        var sut = CreateSut(runner);

        await sut.CommitAsync([], "messaggio", CancellationToken.None);

        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task AddAsync_CostruisceAddForce()
    {
        var runner = new FakeProcessRunner();
        var sut = CreateSut(runner);

        await sut.AddAsync([@"C:\wc\nuovo.txt"], CancellationToken.None);

        Assert.Equal(["add", "--force", @"C:\wc\nuovo.txt"], runner.LastArguments);
    }

    [Fact]
    public async Task RevertAsync_CostruisceRevertDepthInfinity()
    {
        var runner = new FakeProcessRunner();
        var sut = CreateSut(runner);

        await sut.RevertAsync([@"C:\wc\a.cs"], CancellationToken.None);

        Assert.Equal(["revert", "--depth", "infinity", @"C:\wc\a.cs"], runner.LastArguments);
    }

    [Fact]
    public async Task UpdateAsync_PassaWorkingDirectory()
    {
        var runner = new FakeProcessRunner();
        var sut = CreateSut(runner);

        await sut.UpdateAsync(@"C:\wc", CancellationToken.None);

        Assert.Equal(["update"], runner.LastArguments);
        Assert.Equal(@"C:\wc", runner.LastWorkingDirectory);
    }

    [Fact]
    public async Task GetStatusAsync_ParsaXmlERestituisceStati()
    {
        // Arrange — il runner restituisce un XML di status finto
        var runner = new FakeProcessRunner
        {
            Result = new ProcessResult(0, """
                <?xml version="1.0"?>
                <status>
                  <target path=".">
                    <entry path="src/A.cs"><wc-status item="modified" props="none"/></entry>
                    <entry path="src/B.cs"><wc-status item="added" props="none"/></entry>
                    <entry path="cfg"><wc-status item="normal" props="modified"/></entry>
                  </target>
                </status>
                """, string.Empty),
        };
        var sut = CreateSut(runner);

        // Act
        var result = await sut.GetStatusAsync(@"C:\wc", CancellationToken.None);

        // Assert
        Assert.Equal(["status", "--xml"], runner.LastArguments);
        Assert.Equal(3, result.Count);
        Assert.Equal(SvnStatusKind.Modified, result[0].Status);
        Assert.Equal(SvnStatusKind.Added, result[1].Status);
        // item="normal" + props="modified" → trattato come Modified (es. cambio svn:ignore)
        Assert.Equal(SvnStatusKind.Modified, result[2].Status);
    }

    [Fact]
    public async Task GetLogAsync_CostruisceLogXmlConLimite()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ProcessResult(0, "<log></log>", string.Empty),
        };
        var sut = CreateSut(runner);

        await sut.GetLogAsync("file:///repo", 25, CancellationToken.None);

        Assert.Equal(["log", "--xml", "-l", "25", "file:///repo"], runner.LastArguments);
    }

    [Fact]
    public async Task ExitCodeNonZero_LanciaInvalidOperationException()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ProcessResult(1, string.Empty, "svn: E155007: non è una working copy"),
        };
        var sut = CreateSut(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.UpdateAsync(@"C:\wc", CancellationToken.None));
        Assert.Contains("E155007", ex.Message);
    }
}
