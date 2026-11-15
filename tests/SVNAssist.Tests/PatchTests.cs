using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Patch (0.5.0): backup prima dei revert di massa, Create/Apply Patch, esclusione di .svn-assist, giro completo con svn vero.</summary>
public class PatchTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Diff = "Index: a.cs\n===\n--- a.cs\t(revision 1)\n+++ a.cs\t(working copy)\n@@ -1 +1 @@\n-x\n+y\n";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SVNAssist_Patch_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Func<string, long> _free = PatchBackup.FreeSpace;

    public PatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PatchBackup.FreeSpace = _free;
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    // ── PatchBackup ──────────────────────────────────────────────────

    [Fact]
    public void Write_ScriveInSvnAssistBackups_ConDataEOperazione()
    {
        var path = PatchBackup.Write(_dir, "revert", Diff, new DateTime(2026, 11, 15, 10, 30, 5));

        Assert.Equal(Path.Combine(_dir, ".svn-assist", "backups", "20261115-103005-revert.patch"), path);
        Assert.Equal(Diff, File.ReadAllText(path));
    }

    [Fact]
    public void Write_SpazioInsufficiente_LanciaNoSpaceConServonoELiberi_ENonScrive()
    {
        PatchBackup.FreeSpace = _ => 10;

        var ex = Assert.Throws<PatchBackup.NoSpaceException>(() => PatchBackup.Write(_dir, "revert", Diff));

        Assert.Equal(PatchBackup.Needed(Diff.Length), ex.Needed);
        Assert.Equal(10, ex.Free);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".svn-assist")));
    }

    [Fact]
    public void Prune_EliminaIPiuVecchiDiSetteGiorni_ESottoIlTettoTieneIPiuRecenti()
    {
        var folder = PatchBackup.BackupsFolder(_dir);
        Directory.CreateDirectory(folder);
        string Make(string name, int daysOld, int size)
        {
            var p = Path.Combine(folder, name + ".patch");
            File.WriteAllBytes(p, new byte[size]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-daysOld));
            return p;
        }

        var old = Make("old", 8, 10);
        var big1 = Make("big1", 3, (int)(PatchBackup.MaxFolderBytes / 2) + 1);
        var big2 = Make("big2", 2, (int)(PatchBackup.MaxFolderBytes / 2) + 1);
        var recent = Make("recent", 1, 10);

        PatchBackup.Prune(_dir);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(big1));   // il piu' vecchio dei due grossi: la cartella supererebbe 200 MB
        Assert.True(File.Exists(big2));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void BinaryFiles_ElencaIFileCheSvnNonMostraNelDiff()
    {
        var diff = Diff + "Index: img.png\n===\nCannot display: file marked as a binary type.\nsvn:mime-type = application/octet-stream\n";

        Assert.Equal(["img.png"], PatchBackup.BinaryFiles(diff));
        Assert.Empty(PatchBackup.BinaryFiles(Diff));
    }

    [Fact]
    public void ParseStatusXml_IgnoraLaCartellaDeiBackup()
    {
        const string xml = """
            <status><target path="."><entry path=".svn-assist"><wc-status item="unversioned" props="none"/></entry>
            <entry path="x.txt"><wc-status item="modified" props="none"/></entry></target></status>
            """;

        Assert.Equal(["x.txt"], SvnService.ParseStatusXml(xml).Select(s => s.Path));
    }

    [Fact]
    public void SummarizePatch_ContaFileToccatiERifiuti()
    {
        var s = ChangesStore.SummarizePatch("U         a.cs\nA         b.cs\nC         c.cs\n>         rejected hunk @@ -1 +1 @@\nSkipped missing target: 'd'\nSummary of conflicts:\n  Text conflicts: 1\n");

        Assert.Equal(2, s.Touched);
        Assert.Equal(3, s.Problems);
    }

    // ── Store ────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeHost Host { get; } = new();
        public ChangesStore Store { get; }
        public string Wc { get; }

        public Fixture(string wc)
        {
            Wc = wc;
            var settings = new StubSettings();
            Store = new ChangesStore(settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes()))) { WorkingCopyPath = wc, TempRoot = Path.Combine(wc, "tmp") };
            Svn.Statuses.Add(new SvnFileStatus("a.cs", SvnStatusKind.Modified, IsTreeConflict: false));
            Svn.Statuses.Add(new SvnFileStatus("b.cs", SvnStatusKind.Modified, IsTreeConflict: false));
            Svn.PatchText = Diff;
        }

        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
        public ChangesState Snap => (ChangesState)Store.GetSnapshot();
        public string[] Backups => Directory.Exists(PatchBackup.BackupsFolder(Wc)) ? Directory.GetFiles(PatchBackup.BackupsFolder(Wc)) : [];
        public void Dispose() => Store.Dispose();
    }

    private async Task<Fixture> Started()
    {
        var f = new Fixture(_dir);
        await f.Store.RefreshAsync(Ct);
        return f;
    }

    [Fact]
    public async Task RevertDiSezione_SalvaIlBackup_OffreRipristina_ELaProssimaOperazioneLoElimina()
    {
        using var f = await Started();

        await f.Cmd("rowOp", new { op = "revert", key = "section:changes" });

        var backup = Assert.Single(f.Backups);
        Assert.Equal(Diff, File.ReadAllText(backup));
        Assert.Equal("restoreBackup", f.Snap.Status.Action?.Cmd);
        Assert.Equal(2, f.Svn.Reverted.SelectMany(r => r).Count());

        await f.Cmd("update");   // un'altra operazione: il backup non serve piu'

        Assert.Empty(f.Backups);
        Assert.Null(f.Snap.Status.Action);
    }

    [Fact]
    public async Task Ripristina_ApplicaIlBackupEOElimina()
    {
        using var f = await Started();
        await f.Cmd("rowOp", new { op = "revert", key = "section:changes" });
        var backup = Assert.Single(f.Backups);
        f.Svn.PatchOutput = "U         a.cs\nU         b.cs\n";

        await f.Cmd("restoreBackup");

        Assert.Equal([(backup, false)], f.Svn.PatchApplied);
        Assert.Empty(f.Backups);
        Assert.Contains("2", f.Snap.Status.Msg);
        Assert.Null(f.Snap.Status.Action);
    }

    [Fact]
    public async Task RevertDiFileSingolo_NonFaBackup()
    {
        using var f = await Started();

        await f.Cmd("rowOp", new { op = "revert", key = "changes:a.cs" });

        Assert.Empty(f.Backups);
        Assert.Empty(f.Svn.PatchRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpazioInsufficiente_ChiedeSeProcedere_NoAnnullaTutto(bool proceed)
    {
        using var f = await Started();
        PatchBackup.FreeSpace = _ => 5;
        f.Dialogs.AskResult = proceed;

        await f.Cmd("rowOp", new { op = "revert", key = "section:changes" });

        Assert.Contains("Non c'è abbastanza spazio per il backup", Assert.Single(f.Dialogs.Asked));
        Assert.Contains("Procedere senza backup?", f.Dialogs.Asked[0]);
        Assert.Equal(proceed ? 1 : 0, f.Svn.Reverted.Count);
        Assert.Empty(f.Backups);
    }

    [Fact]
    public async Task RevertFallito_IlBackupResta_ELoStatoDiceDove()
    {
        using var f = await Started();
        f.Svn.RevertError = new InvalidOperationException("boom");

        await f.Cmd("rowOp", new { op = "revert", key = "section:changes" });

        var backup = Assert.Single(f.Backups);
        Assert.Contains(backup, f.Snap.Status.Msg);
        Assert.Null(f.Snap.Status.Action);
    }

    [Fact]
    public async Task CreatePatch_ScriveIlFileScelto_ENessunaScelta_NonFaNiente()
    {
        using var f = await Started();
        f.Host.PickedFile = null;
        await f.Cmd("createPatch");
        Assert.Empty(f.Svn.PatchRequests);

        f.Host.PickedFile = Path.Combine(_dir, "out.patch");
        await f.Cmd("createPatch", new { key = "changes:a.cs" });

        Assert.Equal(Diff, File.ReadAllText(f.Host.PickedFile));
        Assert.Equal([Path.Combine(_dir, "a.cs")], Assert.Single(f.Svn.PatchRequests));
        Assert.Contains("out.patch", f.Snap.Status.Msg);
    }

    [Fact]
    public async Task ApplyPatch_AnteprimaPoiApplica_ConConfermaNegataNonApplica()
    {
        using var f = await Started();
        var patch = Path.Combine(_dir, "in.patch");
        f.Host.PickedFile = patch;
        f.Svn.PatchOutput = "U         a.cs\n";
        f.Dialogs.ConfirmResult = false;

        await f.Cmd("applyPatch");

        Assert.Equal([(patch, true)], f.Svn.PatchApplied);   // solo dry-run
        Assert.Contains("a.cs", Assert.Single(f.Dialogs.Messages));

        f.Dialogs.ConfirmResult = true;
        await f.Cmd("applyPatch");

        Assert.Equal([(patch, true), (patch, true), (patch, false)], f.Svn.PatchApplied);
        Assert.Contains("1", f.Snap.Status.Msg);
    }

    // ── svn vero ─────────────────────────────────────────────────────

    private static string Find(string exe)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseSVN", "bin"));
        return dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(exe);
    }

    private static void Run(string exe, string args, string cwd)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(exe + " " + args + ": " + err);
    }

    [Fact]
    public async Task SvnVero_PatchDelRevert_RipristinaLeModifiche()
    {
        var svn = Find("svn.exe");
        var repo = Path.Combine(_dir, "repo");
        var wc = Path.Combine(_dir, "wc");
        Run(Find("svnadmin.exe"), $"create \"{repo}\"", _dir);
        Run(svn, $"checkout \"file:///{repo.Replace('\\', '/')}\" \"{wc}\"", _dir);
        var file = Path.Combine(wc, "t.txt");
        File.WriteAllText(file, "uno\ndue\ntre\n");
        Run(svn, $"add \"{file}\"", wc);
        Run(svn, "commit -m base", wc);
        File.WriteAllText(file, "uno\nDUE\ntre\n");
        var sut = new SvnService(svn);

        var patch = await sut.GetPatchAsync(wc, [file], Ct);
        var backup = PatchBackup.Write(wc, "revert", patch);
        await sut.RevertAsync([file], Ct, recursive: false);
        Assert.Equal("uno\ndue\ntre\n", File.ReadAllText(file).Replace("\r\n", "\n"));

        var dry = await sut.ApplyPatchAsync(wc, backup, dryRun: true, Ct);
        Assert.Equal(1, ChangesStore.SummarizePatch(dry).Touched);
        Assert.Equal("uno\ndue\ntre\n", File.ReadAllText(file).Replace("\r\n", "\n"));   // l'anteprima non scrive
        await sut.ApplyPatchAsync(wc, backup, dryRun: false, Ct);

        Assert.Equal("uno\nDUE\ntre\n", File.ReadAllText(file).Replace("\r\n", "\n"));
        Assert.DoesNotContain(await sut.GetStatusAsync(wc, Ct), s => s.Path.Contains(".svn-assist"));   // il backup non sporca la working copy
    }

    /// <summary>Caso visto dall'utente: un file aggiunto (A) dopo Revert resta '?' su disco e svn patch lo salta; il ripristino lo rimette in A.</summary>
    [Fact]
    public async Task SvnVero_RipristinoDiUnFileAggiunto_LoRimetteInAdd()
    {
        var svn = Find("svn.exe");
        var repo = Path.Combine(_dir, "repo");
        var wc = Path.Combine(_dir, "wc");
        Run(Find("svnadmin.exe"), $"create \"{repo}\"", _dir);
        Run(svn, $"checkout \"file:///{repo.Replace('\\', '/')}\" \"{wc}\"", _dir);
        Directory.CreateDirectory(Path.Combine(wc, "sub"));
        Run(svn, "add sub", wc);
        Run(svn, "commit -m base", wc);
        var added = Path.Combine(wc, "sub", "Nuovo.cs");
        File.WriteAllText(added, "nuovo\n");
        Run(svn, $"add \"{added}\"", wc);
        var sut = new SvnService(svn);

        var patch = await sut.GetPatchAsync(wc, [added], Ct);
        await sut.RevertAsync([added], Ct, recursive: false);
        var backup = PatchBackup.Write(wc, "revert", patch);
        var output = await sut.ApplyPatchAsync(wc, backup, dryRun: false, Ct);

        Assert.True(ChangesStore.AddedSkipped(output, patch).SequenceEqual([Path.Combine("sub", "Nuovo.cs")]), output + "\n=====\n" + patch);
    }
}
