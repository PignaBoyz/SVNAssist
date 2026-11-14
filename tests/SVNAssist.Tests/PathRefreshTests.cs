using SVNAssist.Rpc;

namespace SVNAssist.Tests;

public class PathRefreshTests
{
    [Fact]
    public void Merge_VociNuoveInCoda_OrdineEsistenteInvariato()
        => Assert.Equal(@"C:\a;C:\b;C:\c;D:\d", PathRefresh.Merge(@"C:\a;C:\b", @"C:\c;C:\a", @"D:\d"));

    [Fact]
    public void Merge_DuplicatiCaseInsensitiveEBackslashFinale()
        => Assert.Equal(@"C:\Svn\bin", PathRefresh.Merge(@"C:\Svn\bin", @"c:\svn\BIN\", @"""C:\SVN\bin"""));

    [Fact]
    public void Merge_VociVuoteEdSpaziScartati()
        => Assert.Equal(@"C:\a;C:\b", PathRefresh.Merge(@";C:\a; ;;", "", null!, @" C:\b "));

    [Fact]
    public void Merge_SenzaNulla_Vuoto()
        => Assert.Equal("", PathRefresh.Merge(null!, ""));
}
