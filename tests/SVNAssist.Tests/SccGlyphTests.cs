using SVNAssist.Scc;

namespace SVNAssist.Tests;

public class SccGlyphTests
{
    [Theory]
    [InlineData("Normal", SccGlyph.Normal)]
    [InlineData("Modified", SccGlyph.Modified)]
    [InlineData("Replaced", SccGlyph.Modified)]
    [InlineData("Added", SccGlyph.Added)]
    [InlineData("Deleted", SccGlyph.Deleted)]
    [InlineData("Missing", SccGlyph.Deleted)]
    [InlineData("Conflicted", SccGlyph.Conflict)]
    [InlineData("Unversioned", SccGlyph.Unversioned)]
    [InlineData("Ignored", SccGlyph.None)]
    [InlineData("External", SccGlyph.None)]
    [InlineData(null, SccGlyph.None)]
    public void StatoSvnDiventaGlifo(string? status, SccGlyph expected) => Assert.Equal(expected, SccGlyphMap.For(status!));
}
