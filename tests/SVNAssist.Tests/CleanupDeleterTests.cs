using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Cleanup: eliminazione di non versionati/ignorati fatta da SVNAssist (salta .vs, salta i file in uso).</summary>
public class CleanupDeleterTests
{
    private const string Xml = """
        <status><target path=".">
        <entry path="new.txt"><wc-status item="unversioned" props="none"/></entry>
        <entry path="obj"><wc-status item="ignored" props="none"/></entry>
        <entry path=".vs"><wc-status item="unversioned" props="none"/></entry>
        <entry path="sub\.vs\x.vsidx"><wc-status item="ignored" props="none"/></entry>
        <entry path="a.cs"><wc-status item="modified" props="none"/></entry>
        </target></status>
        """;

    [Fact]
    public void ParseExtras_DistinguePerStatoESaltaVs()
    {
        var root = Path.GetFullPath("wc");
        Assert.Equal([Path.Combine(root, "new.txt"), Path.Combine(root, "obj")], CleanupDeleter.ParseExtras(Xml, root, true, true));
        Assert.Equal([Path.Combine(root, "new.txt")], CleanupDeleter.ParseExtras(Xml, root, true, false));
        Assert.Equal([Path.Combine(root, "obj")], CleanupDeleter.ParseExtras(Xml, root, false, true));
    }

    [Fact]
    public void Delete_FileInUsoSaltato_GliAltriEliminati_Riepilogo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "svnassist-cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "obj", "x"));
        Directory.CreateDirectory(Path.Combine(dir, "obj", ".vs"));
        var free = Path.Combine(dir, "free.txt");
        var busy = Path.Combine(dir, "obj", "busy.dll");
        File.WriteAllText(free, "a");
        File.WriteAllText(busy, "b");
        File.WriteAllText(Path.Combine(dir, "obj", "x", "y.txt"), "c");
        File.WriteAllText(Path.Combine(dir, "obj", ".vs", "keep.vsidx"), "d");
        var lines = new List<string>();
        try
        {
            CleanupReport report;
            using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))   // bloccato come fa VS
                report = CleanupDeleter.Delete([free, Path.Combine(dir, "obj")], lines.Add);

            Assert.False(File.Exists(free));
            Assert.True(File.Exists(busy));
            Assert.True(File.Exists(Path.Combine(dir, "obj", ".vs", "keep.vsidx")));
            Assert.False(Directory.Exists(Path.Combine(dir, "obj", "x")));   // il resto della cartella e' stato eliminato lo stesso
            Assert.Equal(new CleanupReport(1, 1), report);   // free.txt eliminato; obj no (busy + .vs), 1 saltato
            Assert.Contains($"D         {free}", lines);
            Assert.Contains(lines, l => l.Contains("busy.dll") && l.StartsWith("Saltato (in uso)"));
            Assert.Contains("Eliminati 1, saltati 1 perché in uso", report.Summary("Cleanup completato."));
            Assert.Equal("Cleanup completato.", new CleanupReport(0, 0).Summary("Cleanup completato."));
        }
        finally { Directory.Delete(dir, true); }
    }
}
