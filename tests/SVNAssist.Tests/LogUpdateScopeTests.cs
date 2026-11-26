using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Log > tasto destro > Update a questa revisione: cosa si puo' aggiornare a seconda di cosa mostra il log.</summary>
public class LogUpdateScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SVNAssist_UpdScope_" + Guid.NewGuid().ToString("N")[..8]);

    public LogUpdateScopeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".svn"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "f.cpp"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RadiceCartellaFileUrl()
    {
        Assert.Equal("wc", LogStore.UpdateScope(_root));
        Assert.Equal("wc", LogStore.UpdateScope(_root + Path.DirectorySeparatorChar));
        Assert.Equal("dir", LogStore.UpdateScope(Path.Combine(_root, "src")));
        Assert.Equal("file", LogStore.UpdateScope(Path.Combine(_root, "src", "f.cpp")));
        Assert.Null(LogStore.UpdateScope("https://svn.example.com/repo/trunk"));
        Assert.Null(LogStore.UpdateScope(Path.GetTempPath()));
        Assert.Null(LogStore.UpdateScope(""));
    }
}
