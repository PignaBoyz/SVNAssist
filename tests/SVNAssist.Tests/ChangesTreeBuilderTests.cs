using SVNAssist.Models;
using SVNAssist.ToolWindows;

namespace SVNAssist.Tests;

/// <summary>Raggruppamento per sezioni, cartelle compattate, conteggi e checkbox tri-state.</summary>
public class ChangesTreeBuilderTests
{
    private static ChangeEntry Entry(string relative, SvnStatusKind status = SvnStatusKind.Modified, bool tree = false)
        => new(@"C:\wc\" + relative, relative, status, tree);

    [Theory]
    [InlineData(SvnStatusKind.Conflicted, "Conflicts")]
    [InlineData(SvnStatusKind.Modified, "Changes")]
    [InlineData(SvnStatusKind.Missing, "Changes")]
    [InlineData(SvnStatusKind.Obstructed, "Changes")]
    [InlineData(SvnStatusKind.Unversioned, "Unversioned")]
    public void Section_DipendeDallo_Stato(SvnStatusKind status, string expected)
        => Assert.Equal(expected, Entry("a.cs", status).Section.ToString());

    [Fact]
    public void BuildTree_CompattaLeCatenePerCartelleConUnSoloFiglioCartella()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
        [
            Entry(@"Core\Services\A.cs"),
            Entry(@"Core\Services\B.cs"),
        ]);

        var folder = Assert.Single(tree);
        Assert.Equal(@"Core\Services", folder.Label);
        Assert.Equal(@"Core\Services", folder.FolderPath);
        Assert.Equal(2, folder.Count);
        Assert.Equal(["A.cs", "B.cs"], folder.Children.Select(c => c.Label));
    }

    [Fact]
    public void BuildTree_NonCompattaSeLaCartellaHaAnchePiuFile()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
        [
            Entry(@"src\Program.cs"),
            Entry(@"src\Core\Services\A.cs"),
        ]);

        var src = Assert.Single(tree);
        Assert.Equal("src", src.Label);
        // Cartelle prima dei file; la sotto-catena Core\Services è compattata.
        Assert.Equal([@"Core\Services", "Program.cs"], src.Children.Select(c => c.Label));
        Assert.Equal(2, src.Count);
    }

    [Fact]
    public void BuildTree_FileNellaRadiceSonoFigliDiretti()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes, [Entry("b.txt"), Entry("a.txt")]);

        Assert.All(tree, n => Assert.False(n.IsFolder));
        Assert.Equal(["a.txt", "b.txt"], tree.Select(n => n.Label));
    }

    [Fact]
    public void BuildTree_AccettaAncheLo_SlashComeSeparatore()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes, [Entry("a/b/c.cs"), Entry("a/b/d.cs")]);

        Assert.Equal(@"a\b", Assert.Single(tree).Label);
    }

    [Fact]
    public void BuildTree_ChiaveDiCartellaIncludeLaSezione()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Unversioned, [Entry(@"x\y.txt", SvnStatusKind.Unversioned)]);

        Assert.Equal(@"Unversioned|x", tree[0].Key);
    }

    [Fact]
    public void BuildList_OrdinaPerNomeEMantieneLaCartella()
    {
        var list = ChangesTreeBuilder.BuildList(ChangeSection.Changes,
            [Entry(@"z\b.cs"), Entry(@"a\c.cs"), Entry(@"m\A.cs")]);

        Assert.Equal(["A.cs", "b.cs", "c.cs"], list.Select(n => n.Label));
        Assert.Equal(@"m", list[0].Entry!.FolderPath);
    }

    [Fact]
    public void FolderKeys_ElencaTutteLeCartelleAnnidate()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
            [Entry(@"src\a.cs"), Entry(@"src\lib\x\b.cs"), Entry(@"src\lib\y\c.cs")]);

        Assert.Equal(4, ChangesTreeBuilder.FolderKeys(tree).Count());
    }

    [Fact]
    public void GetCheckState_TriStato()
    {
        var a = Entry("a.cs");
        var b = Entry("b.cs");
        var selected = new HashSet<string> { a.FullPath };

        Assert.True(ChangesTreeBuilder.GetCheckState([a], e => selected.Contains(e.FullPath)));
        Assert.False(ChangesTreeBuilder.GetCheckState([b], e => selected.Contains(e.FullPath)));
        Assert.Null(ChangesTreeBuilder.GetCheckState([a, b], e => selected.Contains(e.FullPath)));
        Assert.False(ChangesTreeBuilder.GetCheckState([], _ => true));
    }

    [Fact]
    public void Name_E_FolderPath_DelFile()
    {
        var e = Entry(@"a\b\c.cs");

        Assert.Equal("c.cs", e.Name);
        Assert.Equal(@"a\b", e.FolderPath);
        Assert.Equal(string.Empty, Entry("c.cs").FolderPath);
    }
}
