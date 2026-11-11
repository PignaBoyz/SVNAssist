using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Test della risoluzione elemento Solution Explorer -> working copy (parte pura dei comandi SVN).</summary>
public class SvnTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SVNAssist_Target_" + Guid.NewGuid().ToString("N")[..8]);

    public SvnTargetTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wc", ".svn"));
        Directory.CreateDirectory(Path.Combine(_root, "wc", "src"));
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        File.WriteAllText(Path.Combine(_root, "wc", "src", "a.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "wc", "src", "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(_root, "wc", "App.sln"), "");
        File.WriteAllText(Path.Combine(_root, "plain", "b.cs"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Resolve_FileInUnAlberoMoltoProfondo_TrovaComunqueLaRoot()
    {
        // Prima la ricerca si fermava a 10 livelli: i progetti annidati risultavano "non working copy".
        var deep = Path.Combine(_root, "wc", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l");
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "Deep.cs");
        File.WriteAllText(file, "x");

        var target = SvnTarget.Resolve(file);

        Assert.Equal(Path.Combine(_root, "wc"), target!.WorkingCopyRoot);
    }

    [Fact]
    public void Resolve_PercorsoVuotoOInesistente_RestituisceNull()
    {
        Assert.Null(SvnTarget.Resolve(null));
        Assert.Null(SvnTarget.Resolve("  "));
        Assert.Null(SvnTarget.Resolve(Path.Combine(_root, "non-esiste.cs")));
    }

    [Fact]
    public void Resolve_FileInWorkingCopy_TrovaLaRoot()
    {
        var file = Path.Combine(_root, "wc", "src", "a.cs");

        var target = SvnTarget.Resolve(file);

        Assert.NotNull(target);
        Assert.False(target.IsDirectory);
        Assert.Equal(file, target.Path);
        Assert.Equal(Path.Combine(_root, "wc"), target.WorkingCopyRoot);
        Assert.Equal(Path.Combine(_root, "wc", "src"), target.WorkingDirectory);
    }

    [Fact]
    public void Resolve_Cartella_UsaLaCartellaStessa()
    {
        var folder = Path.Combine(_root, "wc", "src");

        var target = SvnTarget.Resolve(folder);

        Assert.NotNull(target);
        Assert.True(target.IsDirectory);
        Assert.Equal(folder, target.WorkingDirectory);
        Assert.Equal(Path.Combine(_root, "wc"), target.WorkingCopyRoot);
    }

    [Theory]
    [InlineData("wc/App.sln", "wc")]
    [InlineData("wc/src/App.csproj", "wc/src")]
    public void Resolve_SolutionOProgetto_UsaLaCartellaCheLoContiene(string relative, string expectedFolder)
    {
        var target = SvnTarget.Resolve(Path.GetFullPath(Path.Combine(_root, relative)));

        Assert.NotNull(target);
        Assert.True(target.IsDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, expectedFolder)), target.Path);
    }

    [Fact]
    public void Resolve_FuoriDaUnaWorkingCopy_RootNulla()
    {
        var target = SvnTarget.Resolve(Path.Combine(_root, "plain", "b.cs"));

        Assert.NotNull(target);
        Assert.Null(target.WorkingCopyRoot);
    }

    [Fact]
    public void FormatList_OltreIlMassimo_RiassumeIlResto()
    {
        var items = Enumerable.Range(1, 20).Select(i => $"f{i}").ToList();

        var text = SvnTarget.FormatList(items, max: 5);

        Assert.Equal("f1\nf2\nf3\nf4\nf5\n... e altri 15", text);
        Assert.Equal("f1\nf2", SvnTarget.FormatList(["f1", "f2"], max: 5));
    }
}
