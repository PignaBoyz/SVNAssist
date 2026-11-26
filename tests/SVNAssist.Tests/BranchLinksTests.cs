using SVNAssist.Services;

namespace SVNAssist.Tests;

public class BranchLinksTests
{
    [Fact]
    public void PathOf_DaUrlERadice()
    {
        Assert.Equal("/branches/core19_faquesto", BranchLinks.PathOf("https://svn.x/repo/branches/core19_faquesto/", "https://svn.x/repo"));
        Assert.Equal("/branches/con spazio", BranchLinks.PathOf("https://svn.x/repo/branches/con%20spazio", "https://svn.x/repo/"));
        Assert.Equal(string.Empty, BranchLinks.PathOf("https://altro/repo/trunk", "https://svn.x/repo"));
    }

    [Fact]
    public void For_LeggeSoloLaRigaDelRamo()
    {
        const string text = "# commento\n/branches/core19 = rilascio\n/branches/core19_faquesto = 81018\n";

        Assert.Equal(new BranchLink("/branches/core19_faquesto", BranchRole.WorkItem, 81018), BranchLinks.For(text, "/branches/core19_faquesto"));
        Assert.Equal(BranchRole.Release, BranchLinks.For(text, "/branches/core19").Role);
        // Il file copiato in un altro ramo (core20 da core19) non collega il ramo nuovo.
        Assert.Equal(BranchRole.None, BranchLinks.For(text, "/branches/core20").Role);
    }

    [Fact]
    public void With_SostituisceLaPropriaRigaETieneLeAltre()
    {
        var text = BranchLinks.With(string.Empty, new BranchLink("/branches/core19", BranchRole.Release));
        text = BranchLinks.With(text, new BranchLink("/branches/f1", BranchRole.WorkItem, 100));
        text = BranchLinks.With(text, new BranchLink("/branches/f1", BranchRole.WorkItem, 200));

        Assert.StartsWith("#", text);
        Assert.Equal(BranchRole.Release, BranchLinks.For(text, "/branches/core19").Role);
        Assert.Equal(200, BranchLinks.For(text, "/branches/f1").WorkItem);
        Assert.Single(BranchLinks.Parse(text), l => l.Path == "/branches/f1");
    }

    [Fact]
    public void Merge_AggiungeRigheSenzaSostituire()
    {
        // Rientro di f1 in core19: le due righe convivono, ognuno legge la sua.
        var core19 = BranchLinks.With(string.Empty, new BranchLink("/branches/core19", BranchRole.Release));
        var merged = core19 + "/branches/f1 = 81018\r\n";

        Assert.Equal(BranchRole.Release, BranchLinks.For(merged, "/branches/core19").Role);
        Assert.Equal(81018, BranchLinks.For(merged, "/branches/f1").WorkItem);
    }
}
