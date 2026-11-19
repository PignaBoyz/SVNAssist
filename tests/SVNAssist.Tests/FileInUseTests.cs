using SVNAssist.Services;
using Xunit;

namespace SVNAssist.Tests;

public class FileInUseTests
{
    private const string Real = "svn cleanup fallito (exit code 1): svn: E155009: Failed to run the WC DB work queue associated with 'C:\\wc', work item 22327 (file-install a.aps 1 1 1 21 .svn/tmp/svn-1) svn: E720005: Can't move 'C:\\wc\\.svn\\tmp\\svn-2' to 'C:\\wc\\services\\DsMapChecker\\DsMapChecker.aps': Access is denied.";

    [Fact]
    public void RiconosceFileInUsoETrovaIlFile()
    {
        Assert.True(FileInUse.TryParse(Real, out var file));
        Assert.Equal("C:\\wc\\services\\DsMapChecker\\DsMapChecker.aps", file);
    }

    [Fact]
    public void ErroreNonRelatoNonViene_Riconosciuto()
        => Assert.False(FileInUse.TryParse("svn: E170013: Unable to connect to a repository", out _));

    [Fact]
    public void MessaggioSpiegaIlFileEConservaIlCodice()
    {
        var m = FileInUse.Explain("C:\\wc\\a.aps", Real);
        Assert.Contains("a.aps", m);
        Assert.Contains("E155009", m);
        Assert.True(SvnErrors.IsWorkingCopyLocked(m)); // resta il pulsante "Esegui Cleanup"
    }
}
