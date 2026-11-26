using System.Text.Json;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>"Chi ha toccato la riga": parsing di svn blame --xml, righe locali, JSON per l'host, cache per revisione del file.</summary>
public class BlameTests : IDisposable
{
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <blame><target path="f.txt">
        <entry line-number="1"><commit revision="2"><author>anna</author><date>2025-03-01T10:00:00.000000Z</date></commit></entry>
        <entry line-number="2"><commit revision="5"><author>luca</author><date>2026-11-23T11:45:00.000000Z</date></commit></entry>
        <entry line-number="3"><commit revision="2"><author>anna</author><date>2025-03-01T10:00:00.000000Z</date></commit></entry>
        </target></blame>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SVNAssist_Blame_" + Guid.NewGuid().ToString("N")[..8]);

    public BlameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Parse_UnaRigaPerEntry()
    {
        var lines = Blame.Parse(Xml.Trim());

        Assert.Equal([2L, 5L, 2L], lines.Select(l => l.Rev));
        Assert.Equal("luca", lines[1].Author);
        Assert.Equal(2026, lines[1].Date!.Value.Year);
    }

    [Fact]
    public void Map_RigheAggiunteInLocale_SenzaAutore_ELeAltreSpostate()
    {
        var baseLines = Blame.Parse(Xml.Trim());
        var local = "Index: f.txt\n===\n--- f.txt\t(revision 5)\n+++ f.txt\t(working copy)\n@@ -1,3 +1,4 @@\n a\n+nuova\n b\n c\n";

        var mapped = Blame.Map(baseLines, local, 4);

        Assert.Equal(2L, mapped[0]!.Value.Rev);
        Assert.Null(mapped[1]);
        Assert.Equal(5L, mapped[2]!.Value.Rev);
        Assert.Equal(2L, mapped[3]!.Value.Rev);
    }

    [Fact]
    public void ToJson_RevisioniUnicheEIndici()
    {
        var baseLines = Blame.Parse(Xml.Trim());
        using var doc = JsonDocument.Parse(Blame.ToJson([baseLines[0], null, baseLines[1], baseLines[2]]));

        Assert.Equal(2, doc.RootElement.GetProperty("revs").GetArrayLength());
        Assert.Equal([0, -1, 1, 0], doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("luca", doc.RootElement.GetProperty("revs")[1].GetProperty("a").GetString());
    }

    [Fact]
    public async Task GetJson_BlameInCacheFinoAlCambioDiRevisioneDelFile()
    {
        var file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "a\nb\nc\n");
        var svn = new FakeSvnService { BlameXml = Xml.Trim() };
        var blame = new Blame(svn);

        var first = await blame.GetJsonAsync(file, CancellationToken.None);
        await blame.GetJsonAsync(file, CancellationToken.None);
        Assert.Equal(1, svn.BlameCalls);
        Assert.Contains("\"lines\":[0,1,0]", first);

        svn.InfoRevision++;
        await blame.GetJsonAsync(file, CancellationToken.None);
        Assert.Equal(2, svn.BlameCalls);
    }

    [Fact]
    public async Task GetJson_ErroreDiSvn_RestituisceError()
    {
        var svn = new FakeSvnService { BlameXml = "non xml" };
        var file = Path.Combine(_dir, "g.txt");
        File.WriteAllText(file, "x\n");

        using var doc = JsonDocument.Parse(await new Blame(svn).GetJsonAsync(file, CancellationToken.None));

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
