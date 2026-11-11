using SVNAssist.Models;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Raggruppamento in alberi di cartelle e liste, chiavi di riga e conteggi.</summary>
public class ChangesTreeBuilderTests
{
    private static ChangeEntry Entry(string relative, SvnStatusKind status = SvnStatusKind.Modified, bool tree = false, bool dir = false)
        => new(@"C:\wc\" + relative, relative, status, tree, dir);

    [Fact]
    public void BuildTree_CompattaLeCatenePerCartelleConUnSoloFiglioCartella()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
        [
            Entry("Core/Services/A.cs"),
            Entry("Core/Services/B.cs"),
        ]);

        var folder = Assert.Single(tree);
        Assert.Equal("Core/Services", folder.Label);
        Assert.Equal("Core/Services", folder.FolderPath);
        Assert.Equal("changes:Core/Services", folder.Key);
        Assert.Equal(2, folder.Count);
        Assert.Equal(["A.cs", "B.cs"], folder.Children.Select(c => c.Label));
    }

    [Fact]
    public void BuildTree_NonCompattaSeLaCartellaHaAnchePiuFile()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
        [
            Entry("src/Program.cs"),
            Entry("src/Core/Services/A.cs"),
        ]);

        var src = Assert.Single(tree);
        Assert.Equal("src", src.Label);
        // Cartelle prima dei file; la sotto-catena Core/Services e' compattata.
        Assert.Equal(["Core/Services", "Program.cs"], src.Children.Select(c => c.Label));
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
    public void BuildTree_AccettaAncheIlBackslashComeSeparatore()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes, [Entry(@"a\b\c.cs"), Entry(@"a\b\d.cs")]);

        Assert.Equal("a/b", Assert.Single(tree).Label);
    }

    [Fact]
    public void Chiavi_ContengonoSezioneEPercorsoConSlash()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Unversioned,
            [Entry("x/y.txt", SvnStatusKind.Unversioned), Entry("x/z.txt", SvnStatusKind.Unversioned), Entry("r.txt", SvnStatusKind.Unversioned)]);

        Assert.Equal("unversioned:x", tree[0].Key);
        Assert.Equal("unversioned:x/y.txt", tree[0].Children[0].Key);
        Assert.Equal("unversioned:r.txt", tree[1].Key);
    }

    [Fact]
    public void BuildTree_UnaCartellaConFigliElencatiAssorbeLaPropriaVoce()
    {
        // "src" aggiunta (A) con file sotto: niente riga doppia "src" cartella + "src" file, ma una sola cartella che la conta.
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
            [Entry("src", SvnStatusKind.Added, dir: true), Entry("src/a.cs", SvnStatusKind.Added), Entry("src/b.cs", SvnStatusKind.Added)]);

        var src = Assert.Single(tree);
        Assert.True(src.IsFolder);
        Assert.Equal(3, src.Count);
        Assert.Equal(3, src.Entries().Count());
        Assert.Equal(["src", "src/a.cs", "src/b.cs"], src.Entries().Select(e => e.RelativePath));
    }

    [Fact]
    public void BuildList_OrdinaPerNomeEMantieneLaCartella()
    {
        var list = ChangesTreeBuilder.BuildList(ChangeSection.Changes,
            [Entry("z/b.cs"), Entry("a/c.cs"), Entry("m/A.cs")]);

        Assert.Equal(["A.cs", "b.cs", "c.cs"], list.Select(n => n.Label));
        Assert.Equal("m", list[0].Entry!.FolderPath);
    }

    [Fact]
    public void FolderKeys_ElencaTutteLeCartelleAnnidate()
    {
        var tree = ChangesTreeBuilder.BuildTree(ChangeSection.Changes,
            [Entry("src/a.cs"), Entry("src/lib/x/b.cs"), Entry("src/lib/y/c.cs")]);

        Assert.Equal(4, ChangesTreeBuilder.FolderKeys(tree).Count());
    }

    [Fact]
    public void Name_E_FolderPath_DelFile()
    {
        var e = Entry("a/b/c.cs");

        Assert.Equal("c.cs", e.Name);
        Assert.Equal("a/b", e.FolderPath);
        Assert.Equal(string.Empty, Entry("c.cs").FolderPath);
    }

    [Theory]
    [InlineData(SvnStatusKind.Modified, "M")]
    [InlineData(SvnStatusKind.Added, "A")]
    [InlineData(SvnStatusKind.Deleted, "D")]
    [InlineData(SvnStatusKind.Conflicted, "C")]
    [InlineData(SvnStatusKind.Unversioned, "?")]
    [InlineData(SvnStatusKind.Replaced, "R")]
    [InlineData(SvnStatusKind.Missing, "!")]
    [InlineData(SvnStatusKind.Obstructed, "!")]
    [InlineData(SvnStatusKind.Incomplete, "!")]
    public void ShortCode_RestaNellEnumDelloSchema(SvnStatusKind status, string expected)
        => Assert.Equal(expected, StatusPresentation.ShortCode(status));
}
