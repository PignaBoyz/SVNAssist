using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Storia delle righe selezionate: parsing di <c>svn log --diff</c>, mappatura delle righe all'indietro, filtro dei merge.</summary>
public class LineHistoryTests
{
    private const string Sep = "------------------------------------------------------------------------";

    private static string Entry(long rev, string msg, string diff)
    {
        var msgLines = msg.Split('\n');
        return $"{Sep}\nr{rev} | u | 2026-11-26 15:42:05 +0100 (Thu, 26 Nov 2026) | {msgLines.Length} line{(msgLines.Length == 1 ? "" : "s")}\n\n{msg}\n\n{diff}\n";
    }

    private static string Diff(string hunks) => "Index: f.txt\n===================================================================\n--- f.txt\t(revision 1)\n+++ f.txt\t(revision 2)\n" + hunks;

    [Fact]
    public void ParseLogDiff_DivideLeRevisioni_ESaltaIlMessaggio()
    {
        var text = Entry(5, "fix\n" + Sep + "\nIndex: finto", Diff("@@ -1,2 +1,2 @@\n a\n-b\n+B\n")) + Entry(2, "add", Diff("@@ -0,0 +1,2 @@\n+a\n+b\n")) + Sep + "\n";

        var revs = LineHistory.ParseLogDiff(text);

        Assert.Equal([5L, 2L], revs.Select(r => r.Revision));
        Assert.DoesNotContain("finto", revs[0].Diff);
        Assert.Contains("+B", revs[0].Diff);
    }

    [Fact]
    public void ParseLogDiff_UnaRigaDiSoliTrattiniNelCodice_NonChiudeLaRevisione()
    {
        // Riga tolta fatta di 71 trattini: con il '-' del diff diventa identica al separatore di svn.
        var dashes = new string('-', 71);
        var text = Entry(3, "m", Diff($"@@ -1,3 +1,2 @@\n a\n-{dashes}\n c\n")) + Entry(1, "add", Diff("@@ -0,0 +1,3 @@\n+a\n+x\n+c\n")) + Sep + "\n";

        var revs = LineHistory.ParseLogDiff(text);

        Assert.Equal([3L, 1L], revs.Select(r => r.Revision));
        Assert.Contains(" c", revs[0].Diff);
    }

    [Fact]
    public void ParseLogDiff_RevisioneSenzaDiff_HaDiffVuoto()
    {
        var text = Entry(4, "branch", "") + Sep + "\n";

        var revs = LineHistory.ParseLogDiff(text);

        Assert.Single(revs);
        Assert.Empty(LineRevert.ParseHunks(revs[0].Diff));
    }

    [Theory]
    [InlineData(1, false, 1)]   // prima del blocco
    [InlineData(3, true, 3)]    // modificata: la riga vecchia corrispondente
    [InlineData(4, true, null)] // riga in piu' del blocco: nata qui
    [InlineData(6, false, 5)]   // dopo il blocco: una riga in meno sopra
    public void MapBack_BloccoCheSostituisceUnaRigaConDue(int line, bool touched, int? old)
    {
        // a b C c2 d e  <-  a b c d e : riga 3 sostituita da due righe (3 e 4).
        var hunks = LineRevert.ParseHunks("@@ -1,5 +1,6 @@\n a\n b\n-c\n+C\n+c2\n d\n e\n");

        Assert.Equal((touched, old), LineHistory.MapBack(hunks, line));
    }

    [Fact]
    public void MapBack_CancellazionePuraSopra_SpostaLaRiga()
    {
        var hunks = LineRevert.ParseHunks("@@ -1,4 +1,3 @@\n a\n-b\n c\n d\n");

        Assert.Equal((false, (int?)1), LineHistory.MapBack(hunks, 1));
        Assert.Equal((false, (int?)3), LineHistory.MapBack(hunks, 2));
    }

    [Fact]
    public void Trace_SegueLaRigaFinoAllaNascita_ESaltaLeRevisioniCheNonLaToccano()
    {
        var history = new[]
        {
            new LineHistory.RevisionDiff(9, "@@ -1,3 +1,4 @@\n+top\n a\n b\n c\n"),           // aggiunge sopra: la riga scende
            new LineHistory.RevisionDiff(7, "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n"),             // modifica la riga
            new LineHistory.RevisionDiff(5, "@@ -1,3 +1,3 @@\n a\n b\n-c\n+C\n"),             // modifica un'altra riga
            new LineHistory.RevisionDiff(2, "@@ -0,0 +1,3 @@\n+a\n+b\n+c\n"),                // nascita
        };

        var r = LineHistory.Trace("", history, 3, 3); // riga 3 = "B"

        Assert.Equal([7L, 2L], r.Revisions);
        Assert.Equal(0, r.LocalOnly);
    }

    [Fact]
    public void Trace_CopiaDelBranchMostrataComeFileAggiunto_ContinuaSulRamoDiOrigine()
    {
        // Con alcuni server (http) il diff della revisione che crea il branch mostra il file intero come aggiunto.
        var history = new[]
        {
            new LineHistory.RevisionDiff(14121, "@@ -0,0 +1,3 @@\n+a\n+B\n+c\n"),            // svn copy trunk -> branch
            new LineHistory.RevisionDiff(900, "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n"),          // modifica su trunk
            new LineHistory.RevisionDiff(100, "@@ -0,0 +1,3 @@\n+a\n+b\n+c\n"),              // nascita vera: nessuna revisione prima
        };

        Assert.Equal([900L, 100L], LineHistory.Trace("", history, 2, 2).Revisions);
        Assert.Equal([100L], LineHistory.Trace("", history, 1, 1).Revisions);
    }

    [Fact]
    public void Trace_RigheAggiunteInLocale_NonHannoStoria()
    {
        var local = "Index: f.txt\n===================================================================\n--- f.txt\t(revision 2)\n+++ f.txt\t(working copy)\n@@ -1,2 +1,3 @@\n a\n+nuova\n b\n";
        var history = new[] { new LineHistory.RevisionDiff(2, "@@ -0,0 +1,2 @@\n+a\n+b\n") };

        var only = LineHistory.Trace(local, history, 2, 2);
        Assert.Empty(only.Revisions);
        Assert.Equal(1, only.LocalOnly);
        var both = LineHistory.Trace(local, history, 2, 3);
        Assert.Equal([2L], both.Revisions);
        Assert.Equal(1, both.LocalOnly);
    }

    [Fact]
    public void Trace_DicePerOgniRevisioneQualiRigheTocca()
    {
        var history = new[]
        {
            new LineHistory.RevisionDiff(7, "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n"),
            new LineHistory.RevisionDiff(2, "@@ -0,0 +1,3 @@\n+a\n+b\n+c\n"),
        };

        var r = LineHistory.Trace("", history, 2, 3);

        Assert.Equal([2], r.Lines![7]);
        Assert.Equal([2, 3], r.Lines[2]);
    }

    [Fact]
    public void FocusDiff_TieneIntestazioniESoloIBlocchiDelleRighe()
    {
        var diff = "Index: f.txt\n===\n--- f.txt\t(revision 1)\n+++ f.txt\t(revision 2)\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n@@ -40,3 +40,3 @@\n x\n-y\n+Y\n z\n";

        var f = LineHistory.FocusDiff(diff, [41]);

        Assert.Contains("+++ f.txt", f);
        Assert.Contains("+Y", f);
        Assert.DoesNotContain("+B", f);
        Assert.Equal(diff, LineHistory.FocusDiff(diff, [500])); // nessun blocco: diff intero
    }

    [Fact]
    public void OnlyFile_DiUnDiffDellaRevisioneTieneSoloIlFile()
    {
        var diff = "Index: trunk/src/f.txt\n===\n@@ -1 +1 @@\n-a\n+A\nIndex: trunk/altro.txt\n===\n@@ -1 +1 @@\n-x\n+X\n";

        var f = LineHistory.OnlyFile(diff, "src/f.txt");

        Assert.Contains("+A", f);
        Assert.DoesNotContain("altro", f);
    }

    [Fact]
    public void KeepTouching_TieneSoloLeRevisioniMergiateCheToccanoIlFile()
    {
        static SvnLogEntry E(long r, string path, params SvnLogEntry[] children)
            => new(r, "u", DateTimeOffset.UnixEpoch, "m", [new SvnLogPath("M", path)], children);
        var merged = new[]
        {
            E(6, "/trunk/src/f.txt", E(5, "/branches/feat/src/f.txt"), E(3, "/branches/feat")),
            E(4, "/trunk/altro.txt"),
            E(2, "/trunk", E(1, "/branches/x/src/f.txt")),
        };

        var kept = LogStore.KeepTouching(merged, "src/f.txt")!;

        Assert.Equal([6L, 2L], kept.Select(e => e.Revision));
        Assert.Equal([5L], kept[0].Children!.Select(e => e.Revision));
        Assert.Equal([1L], kept[1].Children!.Select(e => e.Revision));
    }

    [Theory]
    [InlineData(125L, 120L)] // la working copy e' tra due revisioni del log: contiene la 120
    [InlineData(200L, 130L)] // piu' recente di tutte: la prima
    [InlineData(120L, 120L)]
    [InlineData(90L, null)]  // piu' vecchia di tutte quelle caricate: nessuna riga
    [InlineData(null, null)] // log di un URL
    public void CurrentRevision_LaPiuRecenteNonSuccessivaAllaWorkingCopy(long? wc, long? expected)
    {
        var entries = new[] { 130L, 120L, 100L }.Select(r => new SvnLogEntry(r, "u", DateTimeOffset.UnixEpoch, "m")).ToList();

        Assert.Equal(expected, LogStore.CurrentRevision(entries, wc));
    }

    [Fact]
    public void BuildLogArgs_ConRevisioni_UnMenoRPerRevisione()
    {
        var args = SvnService.BuildLogArgs("C:\\wc", 50, new SvnLogFilter(IncludeMerged: true, Revisions: [8, 2]));

        Assert.Equal(["-r", "8", "-r", "2"], args.SkipWhile(a => a != "-r").Take(4));
        Assert.DoesNotContain("HEAD:1", args);
        Assert.Contains("-g", args);
    }

    [Fact]
    public void ParseLineRange_RifiutaIntervalliNonValidi()
    {
        Assert.Equal((3, 5), PathCommands.ParseLineRange(["f", "3-5"]));
        Assert.Throws<ArgumentException>(() => PathCommands.ParseLineRange(["f", "5-3"]));
        Assert.Throws<ArgumentException>(() => PathCommands.ParseLineRange(["f", "0-1"]));
        Assert.Throws<ArgumentException>(() => PathCommands.ParseLineRange(["f"]));
    }
}
