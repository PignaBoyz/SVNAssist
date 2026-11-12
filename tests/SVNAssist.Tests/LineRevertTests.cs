using System.Text;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Annullamento di singole righe: parser del diff unified e calcolo (classe pura LineRevert).</summary>
public class LineRevertTests
{
    private static string Diff(string a, string b)
    {
        // diff unified minimo con contesto 1 ottenuto confrontando riga per riga con LCS semplice (solo per i test)
        // le righe tengono la fine riga: "b" e "b\n" sono righe diverse, come per svn (che aggiunge "\ No newline")
        var x = LineRevert.SplitLines(a).ToArray(); var y = LineRevert.SplitLines(b).ToArray();
        static string Bare(string s) => s.TrimEnd('\n');
        var sb = new StringBuilder("Index: f\n--- f\n+++ f\n");
        int[,] l = new int[x.Length + 1, y.Length + 1];
        for (int i = x.Length - 1; i >= 0; i--)
            for (int j = y.Length - 1; j >= 0; j--)
                l[i, j] = x[i] == y[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var body = new List<string>(); int p = 0, q = 0;
        while (p < x.Length || q < y.Length)
            if (p < x.Length && q < y.Length && x[p] == y[q]) { body.Add(" " + Bare(x[p])); p++; q++; }
            else if (q < y.Length && (p == x.Length || l[p, q + 1] >= l[p + 1, q])) body.Add("+" + Bare(y[q++]));
            else body.Add("-" + Bare(x[p++]));
        sb.Append($"@@ -1,{x.Length} +1,{y.Length} @@\n").Append(string.Join("\n", body)).Append('\n');
        return sb.ToString();
    }

    private static string Revert(string baseText, string work, int from, int to, out int count)
    {
        var (t, c) = LineRevert.Apply(work, baseText, LineRevert.ParseHunks(Diff(baseText.Replace("\r", ""), work.Replace("\r", ""))), from, to);
        count = c;
        return t;
    }

    [Fact]
    public void ParseHunks_SeparaIBlocchiDentroUnHunkConContesto()
    {
        var h = LineRevert.ParseHunks("@@ -1,5 +1,5 @@\n a\n-b\n+B\n c\n d\n-e\n+E\n");
        Assert.Equal([new LineRevert.Hunk(2, 1, 2, 1), new LineRevert.Hunk(5, 1, 5, 1)], h);
    }

    [Fact]
    public void RigaAggiunta_VieneTolta()
        => Assert.Equal("a\nb\n", Revert("a\nb\n", "a\nX\nb\n", 2, 2, out var n)); // n verificato sotto

    [Fact]
    public void RigaAggiunta_Conteggio()
    {
        Revert("a\nb\n", "a\nX\nb\n", 2, 2, out var n);
        Assert.Equal(1, n);
    }

    [Fact]
    public void RigaCancellata_ViensRipristinata_ConCursoreSullaRigaAdiacente()
    {
        Assert.Equal("a\nb\nc\n", Revert("a\nb\nc\n", "a\nc\n", 2, 2, out var n)); // cursore su "c", la cancellazione e' subito prima
        Assert.Equal(1, n);
        Assert.Equal("a\nb\nc\n", Revert("a\nb\nc\n", "a\nc\n", 1, 1, out _));
    }

    [Fact]
    public void RigaModificata_TornaComeEra()
    {
        Assert.Equal("a\nb\nc\n", Revert("a\nb\nc\n", "a\nB\nc\n", 2, 2, out var n));
        Assert.Equal(1, n);
    }

    [Fact]
    public void SelezioneParziale_DiUnBlocco_AnnullaSoloLeRigheSelezionate()
    {
        // 3 righe modificate in un blocco, si annulla solo la seconda
        Assert.Equal("a\nB1\nb2\nB3\nz\n", Revert("a\nb1\nb2\nb3\nz\n", "a\nB1\nB2\nB3\nz\n", 3, 3, out var n));
        Assert.Equal(1, n);
    }

    [Fact]
    public void SelezioneParziale_BloccoPiuLungo_RimuoveLeRigheAggiunteSelezionate()
        => Assert.Equal("a\nX\nz\n", Revert("a\nz\n", "a\nX\nY\nz\n", 3, 3, out _));

    [Fact]
    public void PiuBlocchi_SoloQuelliToccatiDallaSelezione()
    {
        var b = "1\n2\n3\n4\n5\n6\n7\n8\n";
        var w = "1\nX\n3\n4\n5\nY\n7\n8\n";
        Assert.Equal("1\n2\n3\n4\n5\nY\n7\n8\n", Revert(b, w, 2, 2, out _));
        Assert.Equal(b, Revert(b, w, 1, 8, out var n));
        Assert.Equal(2, n);
    }

    [Fact]
    public void SelezioneSenzaModifiche_NonCambiaNulla()
    {
        var r = Revert("a\nb\nc\n", "a\nB\nc\n", 3, 3, out var n);
        Assert.Equal("a\nB\nc\n", r);
        Assert.Equal(0, n);
    }

    [Fact]
    public void PrimaEUltimaRigaDelFile()
    {
        Assert.Equal("a\nb\n", Revert("a\nb\n", "X\nb\n", 1, 1, out _));
        Assert.Equal("a\nb\n", Revert("a\nb\n", "a\nX\n", 2, 2, out _));
        Assert.Equal("a\nb\n", Revert("a\nb\n", "a\nb\nc\n", 3, 3, out _));
        Assert.Equal("z\na\nb\n", Revert("z\na\nb\n", "a\nb\n", 1, 1, out _)); // prima riga cancellata
        Assert.Equal("a\nb\nz\n", Revert("a\nb\nz\n", "a\nb\n", 2, 2, out _)); // ultima riga cancellata
    }

    [Fact]
    public void FileSenzaNewlineFinale()
    {
        Assert.Equal("a\nb", Revert("a\nb", "a\nX", 2, 2, out _));      // ultima riga modificata, BASE senza newline
        Assert.Equal("a\nb\n", Revert("a\nb\n", "a\nb", 2, 2, out _));   // la modifica e' solo il newline finale mancante
        Assert.Equal("a\nb", Revert("a\nb", "a\nb\nc", 2, 3, out _));    // (il newline aggiunto a "b" fa parte dello stesso blocco) aggiunta in coda a un file senza newline
    }

    [Fact]
    public void Crlf_MantenutoNelleRigheRipristinate()
    {
        var b = "a\r\nb\r\nc\r\n";
        var w = "a\r\nB\r\nc\r\n";
        var (t, n) = LineRevert.Apply(w, b, LineRevert.ParseHunks(Diff("a\nb\nc\n", "a\nB\nc\n")), 2, 2);
        Assert.Equal(b, t);
        Assert.Equal(1, n);
        Assert.DoesNotContain("\n", t.Replace("\r\n", ""));
    }

    [Fact]
    public void Decode_ConservaBomEEncoding()
    {
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
        var (t, enc, bom) = LineRevert.Decode(utf8Bom);
        Assert.Equal("a", t);
        Assert.Equal(utf8Bom, bom.Concat(enc.GetBytes(t)).ToArray());
        var latin = new byte[] { (byte)'c', 0xE0 }; // "cà" in Windows-1252, UTF-8 non valido
        var (t2, enc2, bom2) = LineRevert.Decode(latin);
        Assert.Equal("cà", t2);
        Assert.Equal(latin, bom2.Concat(enc2.GetBytes(t2)).ToArray());
    }
}
