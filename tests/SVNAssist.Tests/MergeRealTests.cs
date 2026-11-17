using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// Merge su repository file:// veri (svn installato): file coinvolti per revisione, anteprima per revisione e merge con modifiche pendenti
/// (backup, annulla che le ripristina). Dove svn non c'e' i test falliscono come gli altri "SvnVero".
/// </summary>
[Collection("PatchBackup")]
public class MergeRealTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Trunk (wc) con a.txt, b.txt, c.txt, sub/; branch feature con r4 (a.txt), r5 (new.txt), r6 (elimina b.txt), r7 (c.txt + proprieta' su sub).</summary>
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SVNAssist_MgReal2_" + Guid.NewGuid().ToString("N")[..8]);
        public string SvnExe { get; } = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        public string Url { get; private set; } = "";
        public string Wc { get; private set; } = "";
        public string Bwc { get; private set; } = "";
        public FakeDialogs Dialogs { get; } = new();
        public FakeHost Host { get; } = new();
        public ChangesStore Changes { get; private set; } = null!;
        public LogStore Log { get; private set; } = null!;
        public SvnService Svn { get; private set; } = null!;

        public string Run(string args, string? cwd = null, string? exe = null)
        {
            using var p = Process.Start(new ProcessStartInfo(exe ?? SvnExe, args)
            { WorkingDirectory = cwd ?? Wc, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Environment = { ["LC_ALL"] = "C" } })!;
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"svn {args}: {e}");
            return o;
        }

        public string RunTry(string args)
        {
            using var p = Process.Start(new ProcessStartInfo(SvnExe, args)
            { WorkingDirectory = Wc, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Environment = { ["LC_ALL"] = "C" } })!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? o : "(" + p.ExitCode + ")";
        }

        public string Path_(string rel) => Path.Combine(Wc, rel.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string rel, string text) => File.WriteAllText(Path_(rel), text);

        public string Read(string rel) => File.ReadAllText(Path_(rel));

        public Env()
        {
            Directory.CreateDirectory(Root);
            var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
            var repo = Path.Combine(Root, "repo");
            Url = "file:///" + repo.Replace('\\', '/');
            Run($"create \"{repo}\"", Root, admin);
            Run($"mkdir -m init \"{Url}/trunk\" \"{Url}/branches\" \"{Url}/tags\"", Root);
            Wc = Path.Combine(Root, "wc");
            Run($"checkout \"{Url}/trunk\" \"{Wc}\"", Root);
            Write("a.txt", "line1\nline2\nline3\n");
            Write("b.txt", "bee\n");
            Write("c.txt", "cee\n");
            Write("d.txt", "dee\n");
            File.WriteAllBytes(Path_("crlf.txt"), "riga1\r\nriga2\r\n"u8.ToArray());
            File.WriteAllBytes(Path_("latin1.txt"), [0x63, 0x61, 0x66, 0x66, 0xE8, 0x0A]);   // "caffè" in Windows-1252: non e' UTF-8
            File.WriteAllBytes(Path_("bin.dat"), [0x00, 0x01, 0x02, 0xFF, 0x00]);
            Directory.CreateDirectory(Path_("sub"));
            Run("add a.txt b.txt c.txt d.txt crlf.txt latin1.txt bin.dat sub");
            Run("commit -m base");                                                          // r2
            Run($"copy \"{Url}/trunk\" \"{Url}/branches/feature\" -m branch", Root);          // r3
            Bwc = Path.Combine(Root, "bwc");
            Run($"checkout \"{Url}/branches/feature\" \"{Bwc}\"", Root);
            File.WriteAllText(Path.Combine(Bwc, "a.txt"), "line1\nline2 BRANCH\nline3\n");
            Run("commit -m \"feat: modifica a\"", Bwc);                                      // r4
            File.WriteAllText(Path.Combine(Bwc, "new.txt"), "new\n");
            Run("add new.txt", Bwc);
            Run("commit -m \"feat: aggiunge new\"", Bwc);                                    // r5
            Run("rm b.txt", Bwc);
            Run("commit -m \"feat: elimina b\"", Bwc);                                       // r6
            File.WriteAllText(Path.Combine(Bwc, "c.txt"), "cee BRANCH\n");
            Run("propset myprop 1 sub", Bwc);
            Run("commit -m \"feat: c e proprieta su sub\"", Bwc);                            // r7
            Run("update");
            Svn = new SvnService(SvnExe);
            var settings = new StubSettings();
            Changes = new ChangesStore(settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), recentTargetsFile: Path.Combine(Root, "recent.json"))
            { WorkingCopyPath = Wc, TempRoot = Path.Combine(Root, "tmp"), Recent = new RecentMessages(Path.Combine(Root, "recentmsg.json")) };
            Log = new LogStore(Svn, settings, Host, null, Changes);
            Changes.StartMerge = Log.StartMergeAsync;
            Changes.MergeUndone = ct => Log.Merge!.RefreshAsync(ct);
        }

        public Task Cmd(string name, object? args = null) => Changes.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public Task LogCmd(string name, object? args = null) => Log.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public LogState LogSnap => (LogState)Log.GetSnapshot();

        public MergeDto Merge => LogSnap.Merge!;

        public async Task OpenMergeAsync()
        {
            await Changes.RefreshAsync(Ct);
            await Cmd("mergeOpen");
            await Cmd("switchOk", new { name = "branches/feature" });
        }

        public async Task<MergeFilesDto> FilesAsync()
        {
            for (var i = 0; i < 300; i++)
            {
                if (Merge.Files is { Loading: false } f)
                    return f;
                await Task.Delay(20);
            }

            throw new TimeoutException("file non arrivati");
        }

        public void Dispose()
        {
            Changes.Dispose();
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch { /* .svn in sola lettura */ }
        }
    }

    [Fact]
    public async Task SvnVero_FileCoinvolti_PerLaScelta_Unione_DiffDiUnFile_EAnteprimaPerRevisione()
    {
        using var e = new Env();
        MergeStore.FilesDebounce = TimeSpan.Zero;
        await e.OpenMergeAsync();
        Assert.Equal([7L, 6, 5, 4], e.LogSnap.Entries.Select(x => x.Rev));
        Assert.All(e.LogSnap.Entries, x => Assert.Empty(x.Paths));   // niente -v nella lista

        await e.LogCmd("mergeToggle", new { rev = 4 });
        await e.LogCmd("mergeToggle", new { rev = 6 });
        await e.LogCmd("mergeToggle", new { rev = 7 });
        var files = await e.FilesAsync();

        Assert.Equal(["M a.txt", "D b.txt", "M c.txt", "M sub"], files.Items.Select(i => $"{i.A} {i.P}"));
        Assert.Equal("dir", files.Items.Single(i => i.P == "sub").K);
        Assert.Equal(3, files.Files);

        await e.LogCmd("mergeFileOpen", new { p = "a.txt" });
        var diff = e.Host.Diffs.Single();
        Assert.Equal("a.txt r3 ↔ r4", diff.Caption);
        Assert.Contains("BRANCH", File.ReadAllText(diff.Right));
        Assert.DoesNotContain("BRANCH", File.ReadAllText(diff.Left));

        await e.LogCmd("mergeFileOpen", new { p = "b.txt" });   // eliminato in r6: destra vuota
        Assert.Equal(string.Empty, File.ReadAllText(e.Host.Diffs[1].Right));

        await e.LogCmd("mergePreview");
        var by = e.Merge.Result!.ByRev!;
        Assert.Equal([4L, 6, 7], by.Select(g => g.Rev));
        Assert.Equal(["M a.txt"], by[0].Files.Select(f => $"{f.A} {f.P}"));
        Assert.Equal(["D b.txt"], by[1].Files.Select(f => $"{f.A} {f.P}"));
        Assert.Equal("feat: modifica a", by[0].Msg);
        Assert.Empty(await e.Svn.GetLocalChangesAsync(e.Wc, Ct));   // l'anteprima non tocca la working copy
    }

    // ── Merge con modifiche pendenti (bug 7): avviso, backup, annulla che ripristina ──

    /// <summary>Stato "versionati con modifiche" ordinato, byte di ogni file e proprieta' svn:ignore: tutto quello che l'utente potrebbe perdere.</summary>
    private static string Fingerprint(Env e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join("\n", e.Run("status -q").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).Order()));
        foreach (var f in Directory.EnumerateFiles(e.Wc, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".svn" + Path.DirectorySeparatorChar) && !f.Contains(".svn-assist")).Order())
            sb.AppendLine($"{Path.GetRelativePath(e.Wc, f).Replace('\\', '/')} {PreMergeSnapshot.Hash(f)}");
        sb.AppendLine("ignore=" + e.RunTry("propget svn:ignore sub").Trim());
        sb.AppendLine("myprop=" + e.RunTry("proplist sub").Contains("myprop"));
        return sb.ToString();
    }

    /// <summary>Le modifiche pendenti piu' scomode: testo in comune col merge, CRLF, codifica non UTF-8, binario, file vuoto, cartella aggiunta, eliminazione, proprieta' su cartella.</summary>
    private static void MakePending(Env e)
    {
        e.Write("a.txt", "line1\nline2\nline3 USER\n");                              // M, toccato anche da r4 (nessuna riga in comune: unione pulita)
        File.WriteAllBytes(e.Path_("crlf.txt"), "riga1\r\nriga2 MIA\r\n"u8.ToArray());   // M, CRLF
        File.WriteAllBytes(e.Path_("latin1.txt"), [0x63, 0x61, 0x66, 0x66, 0xE8, 0xE8, 0x0A]);   // M, Windows-1252
        File.WriteAllBytes(e.Path_("bin.dat"), [0x00, 0x01, 0x02, 0xFF, 0x00, 0x7F]);       // M, binario
        e.Write("u.txt", "nuovo mio\n");
        File.WriteAllBytes(e.Path_("vuoto.txt"), []);
        Directory.CreateDirectory(e.Path_("nuova"));
        e.Write("nuova/x.txt", "x\n");
        e.Run("add u.txt vuoto.txt nuova");                                               // A (file, vuoto, cartella con file)
        e.Run("rm d.txt");                                                                // D
        e.Run("propset svn:ignore \"*.tmp\" sub");                                        // M sulla cartella (proprieta')
    }

    [Fact]
    public async Task SvnVero_MergeConModifichePendenti_AnnullaRipristinaOgniCosaEsattamente_ECancellaLaCopia()
    {
        using var e = new Env();
        MakePending(e);
        var before = Fingerprint(e);
        await e.OpenMergeAsync();
        Assert.Equal("", e.Merge.Blocked);
        Assert.True(e.Merge.Dirty >= 8);

        await e.LogCmd("mergeAll");

        var ask = e.Dialogs.Messages.Single();
        Assert.Contains("modifiche non committate", ask);
        Assert.Contains("rischio di conflitti", ask);
        Assert.Contains("a.txt", ask);   // il merge tocca a.txt, che l'utente ha gia' modificato
        Assert.Contains("BRANCH", e.Read("a.txt"));
        Assert.Contains("USER", e.Read("a.txt"));   // unione senza conflitti
        var snapshotDir = Directory.GetDirectories(PatchBackup.BackupsFolder(e.Wc), "*-pre-merge").Single();
        Assert.True(File.Exists(Path.Combine(snapshotDir, "changes.patch")));
        Assert.True(File.Exists(Path.Combine(snapshotDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(snapshotDir, "files", "bin.dat")));   // i binari e le codifiche strane stanno nei byte originali
        var changes = (ChangesState)e.Changes.GetSnapshot();
        Assert.Contains("annulla il merge", changes.Merge!.Text);
        Assert.Contains("pendenti", changes.Merge.Text);

        await e.Cmd("mergeUndo");

        Assert.Equal(before, Fingerprint(e));                       // stato, byte, proprieta': tutto come prima del merge
        Assert.False(Directory.Exists(snapshotDir));                // ripristino verificato: la copia non serve piu'
        Assert.Contains("ripristinate", ((ChangesState)e.Changes.GetSnapshot()).Status.Msg);
        Assert.Null(((ChangesState)e.Changes.GetSnapshot()).Merge);
        Assert.Empty(Directory.GetFiles(e.Wc).Where(f => Path.GetFileName(f).StartsWith("a.txt.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SvnVero_MergeConModifichePendenti_ConConflitto_AnnullaRiportaLeModificheDellUtente()
    {
        using var e = new Env();
        e.Write("a.txt", "line1\nline2 MIA\nline3\n");   // stessa riga di r4: conflitto
        e.Write("d.txt", "dee MIA\n");
        var before = Fingerprint(e);
        await e.OpenMergeAsync();

        await e.LogCmd("mergeSelected");   // niente scelta: errore chiaro, nulla di fatto
        await e.LogCmd("mergeToggle", new { rev = 4 });
        await e.LogCmd("mergeSelected");

        Assert.Equal(1, ((ChangesState)e.Changes.GetSnapshot()).Merge!.Conflicts);
        Assert.NotEqual(before, Fingerprint(e));

        await e.Cmd("mergeUndo");

        Assert.Equal(before, Fingerprint(e));
        Assert.Empty(Directory.GetFiles(e.Wc).Where(f => Path.GetFileName(f).StartsWith("a.txt.", StringComparison.Ordinal)));    // niente file di appoggio del conflitto
        Assert.Contains("MIA", e.Read("a.txt"));
    }

    [Fact]
    public async Task SvnVero_RipristinoNonVerificato_TieneLeCopieEDice_DoveSono_EIFileOriginaliSonoComunqueIntatti()
    {
        using var e = new Env();
        MakePending(e);
        var before = Fingerprint(e);
        await e.OpenMergeAsync();
        await e.LogCmd("mergeAll");
        var snapshotDir = Directory.GetDirectories(PatchBackup.BackupsFolder(e.Wc), "*-pre-merge").Single();
        File.Delete(Path.Combine(snapshotDir, "changes.patch"));   // la patch non c'e' piu': proprieta', cartelle ed eliminazioni non tornano dalla patch

        await e.Cmd("mergeUndo");

        var msg = ((ChangesState)e.Changes.GetSnapshot()).Status.Msg;
        Assert.Contains("non è verificato", msg);
        Assert.Contains(snapshotDir, msg);                           // l'utente sa dove sono le sue modifiche
        Assert.True(Directory.Exists(snapshotDir));
        Assert.True(File.Exists(Path.Combine(snapshotDir, "files", "latin1.txt")));
        Assert.Equal(PreMergeSnapshot.Hash(Path.Combine(snapshotDir, "files", "latin1.txt")), PreMergeSnapshot.Hash(e.Path_("latin1.txt")));   // byte originali (non UTF-8) tornati
        Assert.Equal(PreMergeSnapshot.Hash(Path.Combine(snapshotDir, "files", "bin.dat")), PreMergeSnapshot.Hash(e.Path_("bin.dat")));
        Assert.Contains("MIA", e.Read("crlf.txt"));
        _ = before;
    }

    [Fact]
    public async Task SvnVero_CommitDopoUnMergeSuWorkingCopySporca_CancellaLaCopiaDiSicurezza()
    {
        using var e = new Env();
        e.Write("d.txt", "dee MIA\n");
        await e.OpenMergeAsync();
        await e.LogCmd("mergeAll");
        var snapshotDir = Directory.GetDirectories(PatchBackup.BackupsFolder(e.Wc), "*-pre-merge").Single();

        e.Changes.CommitMessage.SetTextQuietly("merge e modifica mia");
        await e.Cmd("commit");

        Assert.Empty(e.Run("status -q").Trim());
        Assert.False(Directory.Exists(snapshotDir));
        Assert.Contains("dee MIA", e.Run("cat d.txt"));
        Assert.Null(((ChangesState)e.Changes.GetSnapshot()).Merge);
    }

    [Fact]
    public async Task SvnVero_ElementoSpostatoOCopiato_BloccaIlMerge_ConSpiegazione_NienteDaPerdere()
    {
        using var e = new Env();
        e.Run("mv d.txt dd.txt");   // aggiunto con storia: revert e patch non la ricreano
        await e.OpenMergeAsync();
        Assert.Contains("aggiunti con storia", e.Merge.Blocked);

        await e.LogCmd("mergeAll");

        Assert.Empty(e.Dialogs.Messages);
        Assert.True(File.Exists(e.Path_("dd.txt")));
        Assert.False(File.Exists(e.Path_("d.txt")));
        Assert.False(Directory.Exists(PatchBackup.BackupsFolder(e.Wc)) && Directory.GetDirectories(PatchBackup.BackupsFolder(e.Wc), "*-pre-merge").Length > 0);
        Assert.Contains("moved", e.Run("status --xml"), StringComparison.OrdinalIgnoreCase);   // e il move e' ancora un move
    }

    [Fact]
    public async Task SvnVero_MergeSenzaModifichePendenti_NonChiedeNulla_NienteCopiaPreMerge_AnnullaComePrima()
    {
        using var e = new Env();
        await e.OpenMergeAsync();
        Assert.Equal(0, e.Merge.Dirty);

        await e.LogCmd("mergeAll");

        Assert.Empty(e.Dialogs.Messages);
        Assert.False(Directory.Exists(PatchBackup.BackupsFolder(e.Wc)) && Directory.GetDirectories(PatchBackup.BackupsFolder(e.Wc), "*-pre-merge").Length > 0);
        await e.Cmd("mergeUndo");
        Assert.Empty(e.Run("status -q").Trim());
    }

    [Fact]
    public async Task SvnVero_MergeFermatoDalConflitto_ChangesSiAggiornaEMostraIConflitti_ErroreUnaSolaVolta()
    {
        using var e = new Env();
        MergeStore.FilesDebounce = TimeSpan.Zero;
        // trunk modifica la stessa riga di a.txt di r4: il merge di r4 va in conflitto e svn si ferma prima di r7 (passate separate)
        e.Write("a.txt", "line1\nline2 TRUNK\nline3\n");
        e.Run("commit -m \"trunk: modifica a\"");
        e.Run("update");
        await e.OpenMergeAsync();
        await e.LogCmd("mergeToggle", new { rev = 4 });
        await e.LogCmd("mergeToggle", new { rev = 7 });

        await e.LogCmd("mergeSelected");

        var changes = (ChangesState)e.Changes.GetSnapshot();
        Assert.NotEmpty(changes.Conflicts ?? []);                      // la sezione Conflitti c'e'
        Assert.Contains(changes.Sections.SelectMany(s => s.Rows), r => r.Status == "C");
        Assert.NotNull(changes.Merge);                                  // e si puo' annullare il merge parziale
        Assert.Contains("fermato", e.Merge.Error);                      // spiegazione in italiano
        Assert.Contains("successive", e.Merge.Error);
        var snap = e.LogSnap;
        Assert.DoesNotContain("E155015", snap.Msg);                      // l'errore non e' ripetuto nella riga in fondo
        Assert.NotNull(changes.Progress);
        Assert.False(changes.Progress!.Running);
    }
}
