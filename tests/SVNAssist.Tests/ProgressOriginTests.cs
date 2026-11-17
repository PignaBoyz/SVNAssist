using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Il progresso porta la vista di origine: finestra completa solo li', striscia nell'altra.</summary>
public class ProgressOriginTests
{
    [Fact]
    public void Origine_DiDefaultChanges_ELogSeIlMergeParteDalLog()
    {
        var p = new OperationProgress();
        p.Begin("update", "SVN Update", "...");
        Assert.Equal("changes", p.GetSnapshot()!.Origin);
        p.Begin("merge", "Merge da x", "...", default, origin: "log");
        Assert.Equal("log", p.GetSnapshot()!.Origin);
        p.Begin("switch", "Switch", "...");
        Assert.Equal("changes", p.GetSnapshot()!.Origin);
    }
}
