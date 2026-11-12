using SVNAssist.Models;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Comandi di Solution Explorer (IBackend.RunPathCommandAsync) con svn e host finti.</summary>
public class PathCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SVNAssist_PC_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeSvnService _svn = new();
    private readonly FakeHost _host = new();
    private readonly PathCommands _cmd;
    private int _refreshes;

    public PathCommandsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wc", ".svn"));
        Directory.CreateDirectory(Path.Combine(_root, "wc", "src"));
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        File.WriteAllText(Path.Combine(_root, "wc", "src", "a.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "wc", "src", "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(_root, "plain", "b.cs"), "x");
        _cmd = new PathCommands(_svn, _host, () => { _refreshes++; return Task.CompletedTask; }, Path.Combine(_root, "tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private Task Run(string command, params string[] paths) => _cmd.RunAsync(command, paths, CancellationToken.None);

    [Fact]
    public async Task Revert_ChiedeConferma_ESeAnnullatoNonTocca()
    {
        _host.PromptAnswers.Enqueue(1); // Annulla
        await Run("revert", P("wc", "src", "a.cs"));

        Assert.Empty(_svn.Reverted);
        Assert.Contains("non è reversibile", _host.Prompts[0].Message);
    }

    [Fact]
    public async Task Revert_Confermato_AnnullaSuFileEAggiorna()
    {
        await Run("revert", P("wc", "src", "a.cs"));

        Assert.Equal([P("wc", "src", "a.cs")], _svn.Reverted.Single());
        Assert.Equal(1, _refreshes);
    }

    [Fact]
    public async Task Csproj_AgisceSullaCartellaChiContiene()
    {
        await Run("ignore", P("wc", "src", "App.csproj"));

        Assert.Equal([P("wc", "src")], _svn.Ignored);
    }

    [Fact]
    public async Task Add_Cartella_AggiungeSoloINonVersionatiSottoDiEssa()
    {
        _svn.Statuses.Add(new SvnFileStatus("src/new.cs", SvnStatusKind.Unversioned));
        _svn.Statuses.Add(new SvnFileStatus("other.txt", SvnStatusKind.Unversioned));
        _svn.Statuses.Add(new SvnFileStatus("src/a.cs", SvnStatusKind.Modified));

        await Run("add", P("wc", "src"));

        Assert.Equal([Path.Combine(P("wc"), "src/new.cs")], _svn.Added.Single());
    }

    [Fact]
    public async Task FuoriDaUnaWorkingCopy_MostraIlMessaggio()
    {
        await Run("update", P("plain", "b.cs"));

        Assert.Contains("working copy", _host.Prompts.Single().Message);
        Assert.Empty(_svn.Reverted);
    }

    [Fact]
    public async Task Diff_File_ApreIlDiffBaseVsWorking()
    {
        await Run("diff", P("wc", "src", "a.cs"));

        Assert.Equal(P("wc", "src", "a.cs"), _host.Diffs.Single().Right);
    }

    [Fact]
    public async Task Diff_Cartella_MostraLElencoNelLog_NonUnSoloDiff()
    {
        await Run("diff", P("wc", "src"));

        Assert.Equal(P("wc", "src"), _host.FolderDiffs.Single());
        Assert.Empty(_host.Diffs);
        Assert.Empty(_host.OpenedFiles);
    }

    [Fact]
    public async Task ComandoSconosciuto_DiventaUnDialogoNonUnaEccezione()
    {
        await Run("boom", P("wc", "src", "a.cs"));

        Assert.Contains("Errore", _host.Prompts.Single().Message);
    }
}
