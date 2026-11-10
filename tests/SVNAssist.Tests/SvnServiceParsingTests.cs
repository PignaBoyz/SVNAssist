using System.Globalization;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Unit test sui parser dell'output di svn, sul timeout/ambiente e sui messaggi d'errore:
/// nessun svn.exe richiesto (XML/testo campione e runner finto).
/// </summary>
public class SvnServiceParsingTests : IDisposable
{
    private const string FakeSvnPath = @"C:\fake\svn.exe";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "SVNAssist_Parse_" + Guid.NewGuid().ToString("N")[..8]);

    public SvnServiceParsingTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public IReadOnlyDictionary<string, string>? LastEnvironment { get; private set; }
        public Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> Handler { get; set; }
            = (_, _) => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(arguments);
            LastEnvironment = environment;
            return Handler(arguments, ct);
        }
    }

    // --- Status (D11) ---

    [Fact]
    public void ParseStatusXml_StatiEstesi_SonoMappatiCorrettamente()
    {
        var xml = """
            <?xml version="1.0"?>
            <status><target path=".">
              <entry path="a.cs"><wc-status item="modified" props="none"/></entry>
              <entry path="cfg"><wc-status item="normal" props="modified"/></entry>
              <entry path="p.txt"><wc-status item="modified" props="conflicted"/></entry>
              <entry path="tree.txt"><wc-status item="missing" props="none" tree-conflicted="true"/></entry>
              <entry path="obj"><wc-status item="obstructed" props="none"/></entry>
              <entry path="half"><wc-status item="incomplete" props="none"/></entry>
              <entry path="bin"><wc-status item="ignored" props="none"/></entry>
              <entry path="ext"><wc-status item="external" props="none"/></entry>
              <entry path="x"><wc-status item="boh" props="none"/></entry>
            </target></status>
            """;

        var result = SvnService.ParseStatusXml(xml);

        // ignored ed external non sono modifiche: scartati
        Assert.Equal(["a.cs", "cfg", "p.txt", "tree.txt", "obj", "half", "x"], result.Select(r => r.Path));
        Assert.Equal(SvnStatusKind.Modified, result[0].Status);
        Assert.Equal(SvnStatusKind.Modified, result[1].Status);
        Assert.Equal(SvnStatusKind.Conflicted, result[2].Status);
        Assert.False(result[2].IsTreeConflict);
        Assert.Equal(SvnStatusKind.Conflicted, result[3].Status);
        Assert.True(result[3].IsTreeConflict);
        Assert.Equal(SvnStatusKind.Obstructed, result[4].Status);
        Assert.Equal(SvnStatusKind.Incomplete, result[5].Status);
        Assert.Equal(SvnStatusKind.Unknown, result[6].Status);
    }

    // --- Log (D4) ---

    [Fact]
    public void ParseLogXml_CulturaNonInvariante_LeggeDataERevisione()
    {
        var xml = """
            <log>
              <logentry revision="1234"><author>mario</author>
                <date>2024-03-15T10:20:30.123456Z</date><msg>fix</msg></logentry>
              <logentry revision="x"><msg>senza data</msg></logentry>
            </log>
            """;
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");

            var entries = SvnService.ParseLogXml(xml);

            Assert.Equal(1234, entries[0].Revision);
            Assert.Equal("mario", entries[0].Author);
            Assert.Equal(new DateTimeOffset(2024, 3, 15, 10, 20, 30, TimeSpan.Zero).AddTicks(1234560), entries[0].Date);
            Assert.Equal(0, entries[1].Revision);
            Assert.Equal(default, entries[1].Date);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // --- Update / commit (D9, D14) ---

    [Fact]
    public void ParseUpdateOutput_ConConflitti_EstraeRevisioneEPercorsi()
    {
        var output = "Updating '.':\nU    src/ok.cs\nC    src/a.txt\n C   props.txt\n   C tree dir\n" +
                     "Updated external to revision 3.\nUpdated to revision 7.\n" +
                     "Summary of conflicts:\n  Text conflicts: 1\n  Property conflicts: 1\n  Tree conflicts: 1\n";

        var result = SvnService.ParseUpdateOutput(output);

        Assert.Equal(7, result.Revision);
        Assert.Equal(["src/a.txt", "props.txt", "tree dir"], result.ConflictedPaths);
    }

    [Fact]
    public void ParseUpdateOutput_GiaAggiornato_RestituisceRevisioneSenzaConflitti()
    {
        var result = SvnService.ParseUpdateOutput("Updating '.':\nAt revision 12.\n");

        Assert.Equal(12, result.Revision);
        Assert.Empty(result.ConflictedPaths);
    }

    [Fact]
    public void ParseCommittedRevision_RiconosceLaRevisione()
    {
        Assert.Equal(42, SvnService.ParseCommittedRevision("Sending        a.cs\nTransmitting file data .\nCommitted revision 42.\n"));
        Assert.Null(SvnService.ParseCommittedRevision(string.Empty));
    }

    [Fact]
    public async Task CommitAsync_RestituisceLaNuovaRevisione()
    {
        var runner = new RecordingRunner
        {
            Handler = (_, _) => Task.FromResult(new ProcessResult(0, "Committed revision 9.\n", string.Empty)),
        };
        var sut = new SvnService(FakeSvnPath, runner);

        var revision = await sut.CommitAsync([@"C:\wc\a.cs"], "msg", CancellationToken.None);

        Assert.Equal(9, revision);
    }

    // --- Ambiente, non-interactive, timeout, errori (D8) ---

    [Fact]
    public async Task Svn_ForzaOutputInglese_ENonInterattivo()
    {
        var runner = new RecordingRunner();
        var sut = new SvnService(FakeSvnPath, runner);

        await sut.UpdateAsync(@"C:\wc", CancellationToken.None);

        Assert.NotNull(runner.LastEnvironment);
        Assert.Equal("C", runner.LastEnvironment!["LC_ALL"]);
        Assert.Equal("C", runner.LastEnvironment["LANG"]);
        Assert.Contains("--non-interactive", runner.Calls[0]);
    }

    [Fact]
    public async Task Svn_TimeoutScaduto_LanciaErroreChiaro()
    {
        var runner = new RecordingRunner
        {
            // Come il runner reale: resta in attesa finche' non viene annullato.
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ProcessResult(0, string.Empty, string.Empty);
            },
        };
        var sut = new SvnService(FakeSvnPath, runner, TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetStatusAsync(@"C:\wc", CancellationToken.None));

        Assert.Contains("non ha risposto", ex.Message);
    }

    [Fact]
    public async Task Svn_AnnullamentoUtente_NonEUnTimeout()
    {
        var runner = new RecordingRunner
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ProcessResult(0, string.Empty, string.Empty);
            },
        };
        var sut = new SvnService(FakeSvnPath, runner);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.UpdateAsync(@"C:\wc", cts.Token));
    }

    [Theory]
    [InlineData("svn: E170013: Unable to connect to a repository", "server SVN")]
    [InlineData("svn: E215004: No more credentials or we tried too many times", "TortoiseSVN")]
    [InlineData("svn: E170001: Authentication required", "svn info")]
    [InlineData("svn: E155004: Working copy locked", "Cleanup")]
    [InlineData("svn: E155011: File 'a' is out of date", "Update")]
    [InlineData("svn: E160024: Transaction is out of date", "Update")]
    [InlineData("svn: Commit failed: item is out of date", "Update")]
    public async Task ErroreSvn_ConCodiceNoto_AggiungeSuggerimentoEMantieneTestoOriginale(string stderr, string expectedHint)
    {
        var runner = new RecordingRunner
        {
            Handler = (_, _) => Task.FromResult(new ProcessResult(1, string.Empty, stderr)),
        };
        var sut = new SvnService(FakeSvnPath, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CommitAsync([@"C:\wc\a.cs"], "m", CancellationToken.None));

        Assert.Contains(expectedHint, ex.Message);
        Assert.Contains(stderr, ex.Message);
    }

    // --- Resolve (D10) ---

    [Theory]
    [InlineData(SvnConflictChoice.MineFull, "mine-full")]
    [InlineData(SvnConflictChoice.TheirsFull, "theirs-full")]
    [InlineData(SvnConflictChoice.Base, "base")]
    [InlineData(SvnConflictChoice.Working, "working")]
    public async Task ResolveAsync_MappaLaScelta(SvnConflictChoice choice, string expected)
    {
        var runner = new RecordingRunner();
        var sut = new SvnService(FakeSvnPath, runner);
        var path = Path.Combine(_tempDir, "nonesiste.txt");

        await sut.ResolveAsync(path, choice, CancellationToken.None);

        Assert.Equal(["resolve", "--non-interactive", "--accept", expected, path], runner.Calls[0]);
    }

    [Fact]
    public async Task ResolveAsync_WorkingConMarcatori_LanciaSenzaInvocareSvn()
    {
        var file = Path.Combine(_tempDir, "conflitto.txt");
        await File.WriteAllTextAsync(file, "a\n<<<<<<< .mine\nmio\n=======\nloro\n>>>>>>> .r5\nb\n");
        var runner = new RecordingRunner();
        var sut = new SvnService(FakeSvnPath, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveAsync(file, SvnConflictChoice.Working, CancellationToken.None));

        Assert.Contains("marcatori", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ResolveAsync_MineFullConMarcatori_RisolveComunque()
    {
        var file = Path.Combine(_tempDir, "conflitto.txt");
        await File.WriteAllTextAsync(file, "<<<<<<< .mine\nmio\n=======\nloro\n>>>>>>> .r5\n");
        var runner = new RecordingRunner();
        var sut = new SvnService(FakeSvnPath, runner);

        await sut.ResolveAsync(file, SvnConflictChoice.MineFull, CancellationToken.None);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public void ContainsConflictMarkers_TitoloMarkdownConUguali_NonEUnConflitto()
    {
        var file = Path.Combine(_tempDir, "doc.md");
        File.WriteAllText(file, "Titolo\n=======\ntesto\n");

        Assert.False(SvnService.ContainsConflictMarkers(file));
    }

    // --- Diff sintetico per file nuovi (D3) ---

    [Fact]
    public void BuildNewFileDiff_FileLungo_LimitaLeRigheESegnalaQuelleOmesse()
    {
        var file = Path.Combine(_tempDir, "nuovo.txt");
        File.WriteAllLines(file, Enumerable.Range(1, 100).Select(i => $"riga {i}"));

        var diff = SvnService.BuildNewFileDiff(file, "nuovo.txt");

        Assert.StartsWith("Index: nuovo.txt\n", diff);
        Assert.Contains("--- nuovo.txt\t(nonexistent)", diff);
        Assert.Contains("@@ -0,0 +1,60 @@", diff);
        Assert.Contains("+riga 60\n", diff);
        Assert.DoesNotContain("+riga 61", diff);
        Assert.Contains("... (40 righe omesse)", diff);
    }

    [Fact]
    public void BuildNewFileDiff_RigaLunghissima_VieneTroncata()
    {
        var file = Path.Combine(_tempDir, "min.js");
        File.WriteAllText(file, new string('x', 5000));

        var diff = SvnService.BuildNewFileDiff(file, "min.js");

        Assert.True(diff.Length < 1000);
    }

    [Fact]
    public void BuildNewFileDiff_Binario_NonIncludeIlContenuto()
    {
        var file = Path.Combine(_tempDir, "img.bin");
        File.WriteAllBytes(file, [0x89, 0x50, 0x00, 0x01, 0x02]);

        var diff = SvnService.BuildNewFileDiff(file, "img.bin");

        Assert.Contains("binario", diff);
        Assert.DoesNotContain("@@", diff);
    }

    [Fact]
    public void BuildNewFileDiff_Cartella_EIndicataComeNuova()
    {
        var diff = SvnService.BuildNewFileDiff(_tempDir, "nuova");

        Assert.Contains("nuova cartella", diff);
    }

    [Fact]
    public void BuildNewFileDiff_FileInesistente_Vuoto()
    {
        Assert.Equal(string.Empty, SvnService.BuildNewFileDiff(Path.Combine(_tempDir, "no.txt"), "no.txt"));
    }

    [Fact]
    public async Task GetDiffForAiAsync_UsaOpzioniMinimaliSoloPerIFileVersionati()
    {
        var newFile = Path.Combine(_tempDir, "nuovo.txt");
        await File.WriteAllTextAsync(newFile, "ciao\n");
        var versioned = Path.Combine(_tempDir, "src", "A.cs");
        var runner = new RecordingRunner
        {
            Handler = (args, _) => Task.FromResult(args[0] == "status"
                ? new ProcessResult(0, """
                    <status><target path="x">
                      <entry path="src\A.cs"><wc-status item="normal" props="none"/></entry>
                      <entry path="nuovo.txt"><wc-status item="unversioned" props="none"/></entry>
                    </target></status>
                    """, string.Empty)
                : new ProcessResult(0, "Index: src\\A.cs\n", string.Empty)),
        };
        var sut = new SvnService(FakeSvnPath, runner);

        var diff = await sut.GetDiffForAiAsync([versioned, newFile], _tempDir, CancellationToken.None);

        var diffCall = runner.Calls.Single(c => c[0] == "diff");
        Assert.Equal(
            ["diff", "--non-interactive", "--depth", "empty", "-x", "-U 0 -p", "--no-diff-deleted", @"src\A.cs"], diffCall);
        Assert.Contains("Index: src\\A.cs", diff);
        Assert.Contains("+ciao", diff);
    }
}
