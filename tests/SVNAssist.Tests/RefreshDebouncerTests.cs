using SVNAssist.ToolWindows;

namespace SVNAssist.Tests;

/// <summary>Debounce del refresh e filtro del rumore del file system.</summary>
public class RefreshDebouncerTests
{
    [Fact]
    public async Task UnaRafficaDiEventi_ProduceUnSoloRefresh()
    {
        var count = 0;
        var done = new TaskCompletionSource();
        var debouncer = new RefreshDebouncer(TimeSpan.FromMilliseconds(300), _ =>
        {
            Interlocked.Increment(ref count);
            done.TrySetResult();
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        var loop = debouncer.RunAsync(cts.Token);

        for (var i = 0; i < 10; i++)
        {
            debouncer.Request(); // raffica senza attese: Task.Delay sui runner CI è troppo impreciso
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        await cts.CancelAsync();
        await loop;

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EventoDuranteIlRefresh_NeProgrammaUnAltro()
    {
        var count = 0;
        RefreshDebouncer? debouncer = null;
        var second = new TaskCompletionSource();
        debouncer = new RefreshDebouncer(TimeSpan.FromMilliseconds(30), _ =>
        {
            if (Interlocked.Increment(ref count) == 1)
                debouncer!.Request();
            else
                second.TrySetResult();
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        var loop = debouncer.RunAsync(cts.Token);

        debouncer.Request();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await loop;

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task UnRefreshCheFallisce_NonFermaIlCiclo()
    {
        var calls = 0;
        var second = new TaskCompletionSource();
        var debouncer = new RefreshDebouncer(TimeSpan.FromMilliseconds(20), _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("svn occupato");
            second.TrySetResult();
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        var loop = debouncer.RunAsync(cts.Token);

        debouncer.Request();
        await Task.Delay(200);
        debouncer.Request();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await loop;
    }

    [Theory]
    [InlineData(@"C:\wc\src\a.cs", false)]
    [InlineData(@"C:\wc\.svn\wc.db", true)]
    [InlineData(@"C:\wc\.svn", true)]
    [InlineData(@"C:\wc\src\bin\Debug\x.dll", true)]
    [InlineData(@"C:\wc\proj\obj\project.assets.json", true)]
    [InlineData(@"C:\wc\.vs\slnx.sqlite", true)]
    [InlineData(@"C:\wc\src\binary.txt", false)]
    [InlineData(@"C:\wc\objects\a.cs", false)]
    public void IsNoise_IgnoraBinObjVsESvn(string path, bool noise)
        => Assert.Equal(noise, WorkingCopyPathFilter.IsNoise(@"C:\wc", path));

    [Fact]
    public void IsNoise_ContaSoloISegmentiSottoLaRoot()
        => Assert.False(WorkingCopyPathFilter.IsNoise(@"C:\bin\wc", @"C:\bin\wc\src\a.cs"));
}
