using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Bug 5 (0.4.0): "Usa entrambe" ricostruisce il file dai marcatori tenendo i due lati nell'ordine scelto.</summary>
public class ConflictBothTests
{
    [Fact]
    public void UnBlocco_MiaPoiLoro_EResto()
    {
        var t = "a\n<<<<<<< .working\nmia1\nmia2\n=======\nloro1\n>>>>>>> .merge-right.r5\nz\n";
        Assert.Equal("a\nmia1\nmia2\nloro1\nz\n", ConflictMarkers.TakeBoth(t, true, out var n));
        Assert.Equal(1, n);
        Assert.Equal("a\nloro1\nmia1\nmia2\nz\n", ConflictMarkers.TakeBoth(t, false, out _));
    }

    [Fact]
    public void PiuBlocchi_RestoInvariato()
    {
        var t = "1\n<<<<<<< .mine\nA\n=======\nB\n>>>>>>> .r3\n2\n<<<<<<< .mine\nC\n=======\nD\n>>>>>>> .r3\n3";
        Assert.Equal("1\nA\nB\n2\nC\nD\n3", ConflictMarkers.TakeBoth(t, true, out var n));
        Assert.Equal(2, n);
        Assert.Equal("1\nB\nA\n2\nD\nC\n3", ConflictMarkers.TakeBoth(t, false, out _));
    }

    [Fact]
    public void Crlf_Preservato()
    {
        var t = "a\r\n<<<<<<< .mine\r\nA\r\n=======\r\nB\r\n>>>>>>> .r3\r\nz\r\n";
        Assert.Equal("a\r\nA\r\nB\r\nz\r\n", ConflictMarkers.TakeBoth(t, true, out _));
    }

    [Fact]
    public void MarcatoriRientratiEConTestoDopo()
    {
        var t = "x\n    <<<<<<< .mine (testo)\n    A\n    ======= \n    B\n    >>>>>>> .r3 altro\ny\n";
        Assert.Equal("x\n    A\n    B\ny\n", ConflictMarkers.TakeBoth(t, true, out _));
    }

    [Fact]
    public void StileDiff3_ScartaLaBase()
    {
        var t = "<<<<<<< .working\nA\n||||||| .merge-left.r1\nBASE\n=======\nB\n>>>>>>> .merge-right.r2\n";
        Assert.Equal("A\nB\n", ConflictMarkers.TakeBoth(t, true, out _));
    }

    [Fact]
    public void UgualiFuoriDaiBlocchi_SonoContenuto()
    {
        var t = "Titolo\n=======\n<<<<<<< .mine\nA\n=======\nB\n>>>>>>> .r3\n";
        Assert.Equal("Titolo\n=======\nA\nB\n", ConflictMarkers.TakeBoth(t, true, out _));
    }

    [Theory]
    [InlineData("niente da fare\n")]
    [InlineData("<<<<<<< .mine\nA\n=======\nB\n")]            // blocco non chiuso
    [InlineData("<<<<<<< .mine\nA\n")]                         // niente =======
    [InlineData("A\n>>>>>>> .r3\n")]                           // chiusura orfana
    [InlineData("<<<<<<< a\n<<<<<<< b\nA\n=======\nB\n>>>>>>> c\n")] // annidato
    public void SenzaMarcatoriValidi_Rifiuta(string t)
    {
        Assert.Null(ConflictMarkers.TakeBoth(t, true, out var n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void File_ByteIdenticiFuoriDaiBlocchi_BomECodificaInclusi()
    {
        var path = Path.Combine(Path.GetTempPath(), "both-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes("caffè\r\n<<<<<<< .mine\r\nmia è\r\n=======\r\nloro ù\r\n>>>>>>> .r2\r\n"));
            File.WriteAllBytes(path, bytes.ToArray());
            Assert.True(ConflictMarkers.TryTakeBoth(path, false, out var n));
            Assert.Equal(1, n);
            var expected = new List<byte> { 0xEF, 0xBB, 0xBF };
            expected.AddRange(System.Text.Encoding.UTF8.GetBytes("caffè\r\nloro ù\r\nmia è\r\n"));
            Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_SenzaMarcatori_NonToccato()
    {
        var path = Path.Combine(Path.GetTempPath(), "both-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "ciao\n");
            Assert.False(ConflictMarkers.TryTakeBoth(path, true, out _));
            Assert.Equal("ciao\n", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
