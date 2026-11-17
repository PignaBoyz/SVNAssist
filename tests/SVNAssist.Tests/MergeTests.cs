using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>SVN Merge (0.3.0 fase 2): parser dell'output testuale, argomenti, selettore, Log in modalita' merge, banner e annulla; in fondo prova con svn vero su un repository file://.</summary>
[Collection("PatchBackup")]
public class MergeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── Parser (campioni catturati da svn 1.14 su un repository file://) ──

    [Fact]
    public void ParseRevisions_UnaPerRiga_ConAsteriscoDelleNonOperative_SenzaDoppioni()
    {
        Assert.Equal([4L, 5, 6, 12], MergeParser.ParseRevisions("r12\r\nr4\nr5*\n  r6  \nr4\n"));
        Assert.Empty(MergeParser.ParseRevisions(""));
        Assert.Empty(MergeParser.ParseRevisions(null));
    }

    private const string DryRun = "--- Merging r3 through r7 into '.':\nU    a.txt\nU    c.txt\nA    new.txt\n U   sub\nD    b.txt\n";

    [Fact]
    public void ParseMerge_DryRun_ElencaLeAzioniConLeColonneDelleProprieta()
    {
        var o = MergeParser.ParseMerge(DryRun);

        Assert.Equal(["U| |a.txt", "U| |c.txt", "A| |new.txt", " |U|sub", "D| |b.txt"], o.Items.Select(i => $"{i.File}|{i.Prop}|{i.Path}"));
        Assert.Equal(' ', o.Items[3].File);   // " U   sub": solo la proprieta'
        Assert.Equal('U', o.Items[3].Prop);
        Assert.Equal(0, o.Conflicts);
        Assert.Equal(0, o.Skipped);
    }

    [Fact]
    public void ParseMerge_MergeVero_LeRigheDiMergeinfoNonSonoFile()
    {
        var o = MergeParser.ParseMerge(DryRun + "--- Recording mergeinfo for merge of r3 through r7 into '.':\n U   .\n--- Eliding mergeinfo from 'x':\n G   y\n");

        Assert.Equal(5, o.Items.Count);
        Assert.DoesNotContain(o.Items, i => i.Path == ".");
    }

    [Fact]
    public void ParseMerge_ConflittoDiTesto_ELeRigheDelRiepilogo()
    {
        var o = MergeParser.ParseMerge("--- Merging r4 into '.':\nC    a.txt\n--- Recording mergeinfo for merge of r4 into '.':\n U   .\nSummary of conflicts:\n  Text conflicts: 1\n");

        var c = Assert.Single(o.Items);
        Assert.True(c.Conflicted);
        Assert.Equal("C", c.Action);
        Assert.Equal(1, o.Conflicts);
    }

    [Fact]
    public void ParseMerge_ConflittoDiProprietaEAdAlbero_ElementiSaltati()
    {
        const string text = "--- Merging r9 through r10 into '.':\n" +
                            " C   dir with space\n" +
                            "   C gone.txt\n" +
                            "G    merged.txt\n" +
                            "Skipped missing target: 'x/y.txt'\n" +
                            "Summary of conflicts:\n" +
                            "  Text conflicts: 0\n" +
                            "  Property conflicts: 1\n" +
                            "  Tree conflicts: 1\n" +
                            "  Skipped paths: 1\n";

        var o = MergeParser.ParseMerge(text);

        Assert.Equal(["dir with space", "gone.txt", "merged.txt"], o.Items.Select(i => i.Path));
        Assert.Equal(["C", "C", "M"], o.Items.Select(i => i.Action));
        Assert.True(o.Items[1].TreeConflict);
        Assert.Equal(2, o.Conflicts);
        Assert.Equal(1, o.Skipped);
    }

    [Fact]
    public void ParseMerge_SenzaRiepilogo_ICoflittiSiContanoDalleRighe_TestoLiberoIgnorato()
    {
        var o = MergeParser.ParseMerge("--- Merging r2 into '.':\nC    a\n C   b\nqualsiasi altra riga\n    \nSkipped 'z'\n");

        Assert.Equal(2, o.Conflicts);
        Assert.Equal(2, o.Items.Count);
        Assert.Equal(1, o.Skipped);
    }

    [Fact]
    public void ToResult_ConteggiPerAzione_ElementiConLaLetteraPerLaUi()
    {
        var o = MergeParser.ParseMerge(DryRun + "C    z.txt\nG    g.txt\n");

        var r = MergeStore.ToResult("preview", "r3-r7", o);

        Assert.Equal(new MergeCountsDto(Updated: 3, Added: 1, Deleted: 1, Merged: 1, Conflicted: 1), r.Counts);
        Assert.Equal(7, r.Items.Count);
        Assert.Equal(["M", "M", "A", "M", "D", "C", "M"], r.Items.Select(i => i.A));
    }

    [Fact]
    public void BuildMergeArgs_DryRun_Cherrypick_PegRevision()
    {
        Assert.Equal(["merge", "--accept", "postpone", "file:///repo/branches/x"], SvnService.BuildMergeArgs("file:///repo/branches/x", null, false));
        Assert.Equal(["merge", "--dry-run", "--accept", "postpone", "-c", "5,7", "file:///repo/branches/x"], SvnService.BuildMergeArgs("file:///repo/branches/x", [5, 7], true));
        Assert.Equal("file:///repo/branches/a@b@", SvnService.BuildMergeArgs("file:///repo/branches/a@b", [], false)[^1]);
    }

    [Theory]
    [InlineData("svn: E195020: Cannot merge into mixed-revision working copy [8:9]; try updating first")]
    public void MessaggioDiErrore_WorkingCopyARevisioniMiste_SuggerisceUpdate(string stderr)
    {
        var msg = SvnService.BuildErrorMessage("merge", 1, stderr);
        Assert.Contains("SVN Update", msg);
        Assert.Contains("E195020", msg);   // il testo di svn resta in chiaro
    }

    // ── Selettore, Log in modalita' merge, banner, annulla ───────────

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public StubSettings Settings { get; } = new();
        public FakeHost Host { get; } = new();
        public ChangesStore Changes { get; }
        public LogStore Log { get; }
        public RecentMessages Recent { get; } = new(Path.Combine(Path.GetTempPath(), "SVNAssist_RecentMsg_" + Guid.NewGuid().ToString("N")[..8] + ".json"));

        public Fixture()
        {
            MergeStore.FilesDebounce = TimeSpan.Zero;
            Svn.InfoRelativeUrl = "^/trunk";
            Svn.Folders["file:///repo"] = ["branches", "tags", "trunk"];
            Svn.Folders["file:///repo/branches"] = ["feature-x", "con spazio"];
            Svn.Folders["file:///repo/tags"] = ["1.0"];
            Svn.Eligible = [4, 5, 7];
            Svn.RangeLog =
            [
                new SvnLogEntry(4, "mrossi", new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), "feat: modifica a", [new SvnLogPath("M", "/branches/feature-x/a.txt", "file")]),
                new SvnLogEntry(5, "lbianchi", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), "feat: aggiunge new\ndettaglio", [new SvnLogPath("A", "/branches/feature-x/new.txt", "file")]),
                new SvnLogEntry(6, "mrossi", new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), "gia unita", []),
                new SvnLogEntry(7, "gverdi", new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero), "feat: elimina b", [new SvnLogPath("D", "/branches/feature-x/b.txt", "file")]),
            ];
            Changes = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())), recentTargetsFile: Path.Combine(Path.GetTempPath(), "SVNAssist_Recent_" + Guid.NewGuid().ToString("N")[..8] + ".json"))
            { WorkingCopyPath = @"C:\wc", TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Mg_" + Guid.NewGuid().ToString("N")[..8]), Recent = Recent };
            Log = new LogStore(Svn, Settings, Host, null, Changes);
            Changes.StartMerge = Log.StartMergeAsync;
            Changes.MergeUndone = ct => Log.Merge!.RefreshAsync(ct);
        }

        public SwitchPickerDto Picker => ((ChangesState)Changes.GetSnapshot()).SwitchPicker;
        public ChangesState ChangesSnap => (ChangesState)Changes.GetSnapshot();
        public LogState LogSnap => (LogState)Log.GetSnapshot();
        public MergeDto Merge => LogSnap.Merge!;
        public static IEnumerable<string> Calls(Fixture f) => f.Svn.Merges.Select(m => $"{(m.Revs is null ? "-" : string.Join(",", m.Revs))} {m.DryRun}");
        public Task Cmd(string name, object? args = null) => Changes.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
        public Task LogCmd(string name, object? args = null) => Log.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public async Task OpenMergeAsync(string source = "branches/feature-x")
        {
            await Cmd("mergeOpen");
            await Cmd("switchOk", new { name = source });
        }

        /// <summary>Dopo un merge svn lascia modifiche locali: lo stato le mostra al refresh.</summary>
        public void MergeLeavesChanges() => Svn.OnMerge.Add(new SvnFileStatus(@"C:\wc\a.txt", SvnStatusKind.Modified, IsTreeConflict: false));

        public void Dispose() => Changes.Dispose();
    }

    [Fact]
    public async Task Selettore_ModoMerge_EscludeIlRamoCorrente_OkApreIlLogInModalitaMerge()
    {
        using var f = new Fixture();

        await f.Cmd("mergeOpen");

        Assert.Equal("merge", f.Picker.Mode);
        Assert.Equal(["branches/feature-x", "branches/con spazio", "tags/1.0"], f.Picker.Items.Select(i => i.Name));   // niente trunk: e' il ramo corrente
        await f.Cmd("switchOk", new { name = "trunk" });
        Assert.True(f.Picker.Open);   // non e' in elenco: errore, niente merge
        Assert.False(f.Log.Merge!.Active);

        await f.Cmd("switchOk", new { name = "branches/feature-x" });

        Assert.False(f.Picker.Open);
        Assert.Equal(["Merge da branches/feature-x"], f.Host.LogWindowTitles);
        Assert.Empty(f.Svn.Switched);   // e' un merge, non uno switch
        Assert.Equal("Merge da branches/feature-x", f.Merge.Title);
        Assert.Equal(3, f.Merge.Eligible);
        Assert.Equal([7L, 5, 4], f.LogSnap.Entries.Select(e => e.Rev));   // la 6 e' gia' unita: non e' tra le eligible
        Assert.Equal(["gverdi", "lbianchi", "mrossi"], f.LogSnap.Entries.Select(e => e.Author));
        Assert.Null(f.LogSnap.Sel);
        Assert.Equal("", f.Merge.Blocked);
    }

    [Fact]
    public async Task Selettore_ModoSwitchDopoUnMerge_TornaAdEssereUnSwitch()
    {
        using var f = new Fixture();
        await f.Cmd("mergeOpen");
        await f.Cmd("switchCancel");

        await f.Cmd("switchOpen");

        Assert.Equal("switch", f.Picker.Mode);
        Assert.Contains("trunk", f.Picker.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Merge_SvnVecchio_Rifiutato()
    {
        using var f = new Fixture();
        f.Svn.SvnVersion = new Version(1, 7, 22);

        await f.OpenMergeAsync();

        Assert.Contains("1.8", f.Merge.Error);
        Assert.Empty(f.LogSnap.Entries);
    }

    // ── Prestazioni (0.5.0, "merge lento"): niente -v nella lista, log a gruppi e in cache, status senza non versionati ──

    [Fact]
    public async Task Merge_Caricamento_LogSenzaVerbose_UnaRichiestaPerGruppo_StatusSenzaNonVersionati()
    {
        using var f = new Fixture();

        await f.OpenMergeAsync();

        Assert.Equal([(4L, 7L, false)], f.Svn.RangeCalls);   // un solo giro, senza -v (i file si leggono solo per le revisioni scelte)
        Assert.All(f.LogSnap.Entries, e => Assert.Empty(e.Paths));
        Assert.Equal(1, f.Svn.LocalChangesCalls);
    }

    [Fact]
    public async Task Merge_DopoIlMerge_SiRileggeSoloMergeinfo_IlLogDelleRevisioniGiaViste_EInCache()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();
        f.Svn.EligibleAfterMerge = [5];
        f.Svn.RangeCalls.Clear();

        await f.LogCmd("mergeAll");

        Assert.Empty(f.Svn.RangeCalls);   // la 5 era gia' in cache: nessun svn log
        Assert.Equal([5L], f.LogSnap.Entries.Select(e => e.Rev));
        Assert.Equal("lbianchi", f.LogSnap.Entries[0].Author);
    }

    [Fact]
    public void GroupRevisions_VicineInUnGruppo_LontaneSeparate()
    {
        Assert.Equal([(4L, 7L)], MergeStore.GroupRevisions([4, 5, 7]));
        Assert.Equal([(10L, 20L), (5000L, 5001L)], MergeStore.GroupRevisions([10, 12, 20, 5000, 5001]));
        Assert.Empty(MergeStore.GroupRevisions([]));
    }

    [Fact]
    public void BuildMergeArgs_RevisioniConsecutive_UnIntervalloSolo_UnaPassataDiSvn()
    {
        Assert.Equal("5-7,9", SvnService.BuildMergeArgs("file:///r", [5, 6, 7, 9], false)[^2]);
        Assert.Equal(["log", "--xml", "-r", "9:4", "file:///r"], SvnService.BuildLogRangeArgs("file:///r", 4, 9, withPaths: false));
        Assert.Equal(["log", "--xml", "-v", "-r", "9:4", "file:///r"], SvnService.BuildLogRangeArgs("file:///r", 4, 9, withPaths: true));
    }

    [Fact]
    public async Task Avanzamento_MilleRighe_NonSonoMilleNotifiche()
    {
        var progress = new OperationProgress();
        var notifications = 0;
        progress.Changed += () => Interlocked.Increment(ref notifications);
        progress.Begin("merge", "Merge", "...");
        var before = notifications;

        for (var i = 0; i < 2000; i++)
            progress.Line($"U    src/f{i}.txt");
        await Task.Delay(OperationProgress.LineNotifyInterval + TimeSpan.FromMilliseconds(250));

        Assert.True(notifications - before < 10, $"notifiche: {notifications - before}");   // la prima subito e una in coda con l'ultimo stato
        Assert.Equal(2000, progress.GetSnapshot()!.Processed);
        Assert.Equal(OperationProgress.MaxLines, progress.GetSnapshot()!.Lines.Count);
    }

    [Fact]
    public async Task Merge_SnapshotDelLog_SenzaPercorsi_ResteLeggero()
    {
        using var f = new Fixture();
        f.Svn.Eligible = Enumerable.Range(1, 600).Select(r => (long)r).ToList();
        f.Svn.RangeLog = f.Svn.Eligible.Select(r => new SvnLogEntry(r, "mrossi", DateTimeOffset.UnixEpoch, "fix " + r,
            Enumerable.Range(0, 30).Select(i => new SvnLogPath("M", $"/branches/feature-x/src/d{i}/file{r}.cs", "file")).ToList())).ToList();

        await f.OpenMergeAsync();

        var json = UiJson.Serialize(f.Log.GetSnapshot());
        Assert.True(json.Length < 120_000, $"snapshot di {json.Length} byte");   // con -v erano ~1,6 MB per 600 revisioni di 30 file
    }

    // ── File coinvolti (bug 6): elenco per la scelta, per revisione e nell'anteprima ──

    private static async Task<MergeFilesDto> FilesAsync(Fixture f)
    {
        for (var i = 0; i < 200; i++)
        {
            if (f.Merge.Files is { Loading: false } files)
                return files;
            await Task.Delay(10);
        }

        throw new TimeoutException("i file non sono arrivati");
    }

    private static SvnLogPath P(string a, string p, string k = "file") => new(a, p, k);

    [Fact]
    public void MergeFiles_Unione_EffettoNettoPerPercorso()
    {
        var map = new Dictionary<long, IReadOnlyList<SvnLogPath>>
        {
            [1] = [P("A", "/branches/x/new.txt"), P("M", "/branches/x/a.txt"), P("D", "/branches/x/old.txt"), P("A", "/branches/x"), P("M", "/trunk/altro.txt")],
            [2] = [P("M", "/branches/x/new.txt"), P("D", "/branches/x/a.txt"), P("A", "/branches/x/old.txt"), P("A", "/branches/x/tmp.txt")],
            [3] = [P("D", "/branches/x/tmp.txt"), P("M", "/branches/x/src", "dir")],
        };

        var items = MergeFiles.Union([3, 1, 2], r => map[r], "branches/x");

        Assert.Equal(new[] { "A new.txt x2", "D a.txt x2", "R old.txt x2", "M src x1", "M /trunk/altro.txt x1" }.Order(), items.Select(i => $"{i.A} {i.P} x{i.N}").Order());
        Assert.DoesNotContain(items, i => i.P == "tmp.txt");   // aggiunto e poi eliminato: nessun effetto
        Assert.Equal("dir", items.Single(i => i.P == "src").K);
        Assert.Null(MergeFiles.Relative("/branches/x", "branches/x"));
        Assert.Equal("a/b.cs", MergeFiles.Relative("/branches/x/a/b.cs", "branches/x"));
    }

    [Fact]
    public async Task Merge_ScelgoLeRevisioni_FileInAsincrono_UnioneConConteggio_PoiInCache()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        Assert.Null(f.Merge.Files);   // niente scelta, niente elenco, e nessun log -v finora
        Assert.DoesNotContain(f.Svn.RangeCalls, c => c.WithPaths);

        await f.LogCmd("mergeToggle", new { rev = 4 });
        await f.LogCmd("mergeToggle", new { rev = 7 });
        var files = await FilesAsync(f);

        Assert.Equal("r4, r7", files.Subject);
        Assert.Equal(["M a.txt", "D b.txt"], files.Items.Select(i => $"{i.A} {i.P}"));
        Assert.Equal(2, files.Files);
        Assert.Equal(2, f.Svn.RangeCalls.Count(c => c.WithPaths));   // 4 e 7 non sono adiacenti nell'elenco (la 5 sta in mezzo): due richieste
        var before = f.Svn.RangeCalls.Count;

        await f.LogCmd("mergeToggle", new { rev = 7 });   // toglie la 7
        await f.LogCmd("mergeToggle", new { rev = 7 });   // la rimette: in cache
        files = await FilesAsync(f);
        Assert.Equal(before, f.Svn.RangeCalls.Count);
        Assert.Equal(["M a.txt", "D b.txt"], files.Items.Select(i => $"{i.A} {i.P}"));
        Assert.Equal(1, f.Svn.RangeCalls.Count(c => c.WithPaths && c.Min == 4 && c.Max == 4));
    }

    [Fact]
    public async Task Merge_MenuContestuale_MostraFileDiUnaRevisione_SenzaToccareLaScelta()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        await f.LogCmd("mergeToggle", new { rev = 4 });
        await FilesAsync(f);

        await f.LogCmd("mergeFocus", new { rev = 5 });
        var files = await FilesAsync(f);

        Assert.True(files.Focus);
        Assert.Equal("r5", files.Subject);
        Assert.Equal(["A new.txt"], files.Items.Select(i => $"{i.A} {i.P}"));
        Assert.Equal([4L], f.Merge.Selected);
        await f.LogCmd("mergeFocus", new { rev = 0 });
        files = await FilesAsync(f);
        Assert.False(files.Focus);
        Assert.Equal("r4", files.Subject);
    }

    [Fact]
    public async Task Merge_ClicSuUnFile_ApreIlDiff_PrimaDellaPrimaEDopoLUltimaRevisione()
    {
        using var f = new Fixture();
        f.Svn.RangeLog = [.. f.Svn.RangeLog, new SvnLogEntry(8, "mrossi", DateTimeOffset.UnixEpoch, "ancora a", [P("M", "/branches/feature-x/a.txt")])];
        f.Svn.Eligible = [4, 5, 7, 8];
        await f.OpenMergeAsync();
        await f.LogCmd("mergeToggle", new { rev = 4 });
        await f.LogCmd("mergeToggle", new { rev = 8 });
        var files = await FilesAsync(f);
        Assert.Equal(2, files.Items.Single(i => i.P == "a.txt").N);

        await f.LogCmd("mergeFileOpen", new { p = "a.txt" });

        Assert.Equal("a.txt r3 ↔ r8", f.Host.Diffs.Single().Caption);   // 4-1 e 8
        Assert.Contains(f.Svn.ExportedRevisions, x => x.Target == "file:///repo/branches/feature-x/a.txt");

        await f.LogCmd("mergeFileOpen", new { p = "a.txt", rev = 4 });
        Assert.Equal("a.txt r3 ↔ r4", f.Host.Diffs[1].Caption);

        await f.LogCmd("mergeFileOpen", new { p = "inesistente.txt" });
        Assert.Contains("non presente", f.Merge.Error);
        Assert.Equal(2, f.Host.Diffs.Count);
    }

    [Fact]
    public async Task Anteprima_PerRevisione_FileDiOgniRevisioneEConflittiDelDryRun()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge("--- Merging r4 through r7 into '.':\nC    a.txt\nA    new.txt\nD    b.txt\n");
        await f.OpenMergeAsync();

        await f.LogCmd("mergePreview");

        var r = f.Merge.Result!;
        Assert.Equal([4L, 5, 7], r.ByRev!.Select(g => g.Rev));   // dalla piu' vecchia: l'ordine in cui il merge le applica
        Assert.Equal("mrossi", r.ByRev![0].Author);
        Assert.Equal("feat: modifica a", r.ByRev[0].Msg);
        Assert.Equal(["M a.txt"], r.ByRev[0].Files.Select(x => $"{x.A} {x.P}"));
        Assert.True(r.ByRev[0].Files[0].C);   // a.txt e' in conflitto nel dry-run
        Assert.Equal(1, r.ByRev[0].Conflicts);
        Assert.False(r.ByRev[1].Files[0].C);
        Assert.Equal("feat: aggiunge new", r.ByRev[1].Msg);   // solo la prima riga del messaggio
        Assert.Equal(1, r.Counts.Conflicted);
    }

    [Fact]
    public async Task MenuContestuale_AnteprimaEMergeDiUnaSolaRevisione_LaScelta_DiventaQuella()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge(DryRun);
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();
        await f.LogCmd("mergeToggle", new { rev = 4 });

        await f.LogCmd("mergePreviewRev", new { rev = 5 });
        Assert.Equal([5L], f.Merge.Selected);
        Assert.Equal("preview", f.Merge.Result!.Mode);
        Assert.Equal([5L], f.Merge.Result.ByRev!.Select(g => g.Rev));

        await f.LogCmd("mergeRev", new { rev = 7 });
        Assert.Equal(["5 True", "7 False"], Fixture.Calls(f));
        Assert.Equal("merge", f.Merge.Result!.Mode);
        Assert.Null(f.Merge.Files);   // dopo il merge la scelta si azzera
    }

    [Fact]
    public async Task Snapshot_ConFileEAnteprimaPerRevisione_RispettanoLoSchema()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge(DryRun + "C    a.txt\n");
        await f.OpenMergeAsync();
        await f.LogCmd("mergeToggle", new { rev = 5 });
        await FilesAsync(f);
        await f.LogCmd("mergePreview");

        var json = UiJson.Serialize(f.Log.GetSnapshot());

        Assert.Contains("\"files\"", json);
        Assert.Contains("\"byRev\"", json);
        var errors = SnapshotSchemaTests.Validate("log-state", json);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Merge_ConflittiOFileMancanti_Bloccano_ConSpiegazione_NienteMerge()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\x.cs", SvnStatusKind.Conflicted, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\m.cs", SvnStatusKind.Missing, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\nuovo.txt", SvnStatusKind.Unversioned, IsTreeConflict: false));   // i non versionati non contano
        await f.OpenMergeAsync();
        Assert.Contains("2 elementi in conflitto, mancanti, a metà", f.Merge.Blocked);

        await f.LogCmd("mergeAll");

        Assert.Empty(f.Svn.Merges);
        Assert.Contains("Risolvili", f.Merge.Blocked);
        Assert.Null(f.ChangesSnap.Merge);

        f.Svn.Statuses.Clear();   // ripulita: il blocco cade al tentativo successivo
        await f.LogCmd("mergeAll");
        Assert.Single(f.Svn.Merges, m => !m.DryRun);
    }

    [Fact]
    public async Task Merge_ConModifichePendenti_NonBlocca_ChiedeConfermaConFileInComune_ENegataNonFaNiente()
    {
        using var f = new Fixture();
        var wc = Path.Combine(Path.GetTempPath(), "SVNAssist_MgDirty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wc);
        try
        {
            f.Changes.WorkingCopyPath = wc;
            File.WriteAllText(Path.Combine(wc, "a.txt"), "mio\n");
            File.WriteAllText(Path.Combine(wc, "altro.txt"), "altro\n");
            f.Svn.Statuses.Add(new SvnFileStatus("a.txt", SvnStatusKind.Modified, IsTreeConflict: false));
            f.Svn.Statuses.Add(new SvnFileStatus("altro.txt", SvnStatusKind.Modified, IsTreeConflict: false));
            f.Svn.MergeResult = MergeParser.ParseMerge("--- Merging r4 through r7 into '.':\nU    a.txt\nA    new.txt\n");
            f.Svn.PatchText = "Index: a.txt\n";
            await f.OpenMergeAsync();
            Assert.Equal("", f.Merge.Blocked);
            Assert.Equal(2, f.Merge.Dirty);   // l'interfaccia lo mostra
            f.Dialogs.ConfirmResult = false;

            await f.LogCmd("mergeAll");

            var msg = f.Dialogs.Messages.Single();
            Assert.Contains("2 elementi con modifiche non committate", msg);
            Assert.Contains("copia di sicurezza", msg);
            Assert.Contains("rischio di conflitti: a.txt", msg);   // a.txt e' toccato dal merge; altro.txt no
            Assert.DoesNotContain("altro.txt", msg);
            Assert.Equal(["- True"], Fixture.Calls(f));   // solo il dry-run per i file in comune: il merge vero non parte
            Assert.False(Directory.Exists(PatchBackup.BackupsFolder(wc)));   // nessuna copia, nulla toccato
            Assert.Contains("non sono state toccate", f.LogSnap.Msg);
        }
        finally
        {
            Directory.Delete(wc, true);
        }
    }

    [Fact]
    public async Task Merge_ConModifichePendenti_SenzaSpazioPerLaCopia_NonParteMai_NienteDomandaSenzaBackup()
    {
        using var f = new Fixture();
        var wc = Path.Combine(Path.GetTempPath(), "SVNAssist_MgDirty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wc);
        var oldFree = PatchBackup.FreeSpace;
        try
        {
            PatchBackup.FreeSpace = _ => 10;
            f.Changes.WorkingCopyPath = wc;
            File.WriteAllText(Path.Combine(wc, "a.txt"), "mio\n");
            f.Svn.Statuses.Add(new SvnFileStatus("a.txt", SvnStatusKind.Modified, IsTreeConflict: false));
            f.Svn.PatchText = "Index: a.txt\n";
            await f.OpenMergeAsync();

            await f.LogCmd("mergeAll");

            Assert.Equal(["- True"], Fixture.Calls(f));   // solo il dry-run: il merge vero non e' partito
            Assert.Contains("non c'è abbastanza spazio", f.Merge.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("non è stato toccato nulla", f.Merge.Error);
            Assert.Empty(f.Dialogs.Asked);   // mai "procedere senza backup?" con modifiche pendenti
            Assert.Null(f.ChangesSnap.Merge);
            Assert.False(Directory.Exists(Path.Combine(PatchBackup.BackupsFolder(wc), "x")));
        }
        finally
        {
            PatchBackup.FreeSpace = oldFree;
            Directory.Delete(wc, true);
        }
    }

    private const string E155015 = "svn: E155015: One or more conflicts were produced while merging r21:22 into '/wc' -- resolve all conflicts and rerun the merge to apply the remaining unmerged revisions";

    /// <summary>Working copy vera su disco (serve la copia di sicurezza) con a.txt e altro.txt modificati.</summary>
    private static string DirtyWc(Fixture f)
    {
        var wc = Path.Combine(Path.GetTempPath(), "SVNAssist_MgDirty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wc);
        f.Changes.WorkingCopyPath = wc;
        File.WriteAllText(Path.Combine(wc, "a.txt"), "mio\n");
        File.WriteAllText(Path.Combine(wc, "altro.txt"), "altro\n");
        f.Svn.Statuses.Add(new SvnFileStatus("a.txt", SvnStatusKind.Modified, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus("altro.txt", SvnStatusKind.Modified, IsTreeConflict: false));
        f.Svn.PatchText = "Index: a.txt\n";
        return wc;
    }

    [Fact]
    public async Task ModifichePendenti_DryRunDeiFileInComuneFermatoDaE155015_NonEUnMergeFermato_NienteBanner_ConfermaDalLog()
    {
        using var f = new Fixture();
        var wc = DirtyWc(f);
        try
        {
            f.Svn.DryRunError = E155015;   // il dry-run (non il merge vero) si ferma su un conflitto
            f.Dialogs.ConfirmResult = false;
            await f.OpenMergeAsync();

            await f.LogCmd("mergeAll");

            Assert.Null(f.ChangesSnap.Merge);                                  // nessun "Annulla merge": non e' stato toccato nulla
            Assert.Empty(f.Svn.Merges);
            Assert.Equal("", f.Merge.Error);                                   // e nessun errore di merge fermato
            var msg = f.Dialogs.Messages.Single();                             // la conferma c'e' lo stesso, con i file in comune presi dal log (r4 tocca a.txt)
            Assert.Contains("rischio di conflitti: a.txt", msg);
            Assert.False(Directory.Exists(PatchBackup.BackupsFolder(wc)));
        }
        finally
        {
            Directory.Delete(wc, true);
        }
    }

    [Fact]
    public async Task ModifichePendenti_MergeVeroFallitoDopoLaCopia_ResteAnnullaMerge_CheRipristina_NonSiTienePiuUnaCopiaDiUnaAltraWorkingCopy()
    {
        using var f = new Fixture();
        var wc = DirtyWc(f);
        var other = Path.Combine(Path.GetTempPath(), "SVNAssist_MgOther_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(other);
        try
        {
            f.Svn.RealMergeError = "svn: E170013: Unable to connect to a repository";
            await f.OpenMergeAsync();

            await f.LogCmd("mergeAll");

            Assert.Contains("E170013", f.Merge.Error);
            Assert.Contains("non è andato a buon fine", f.ChangesSnap.Merge!.Text);   // le modifiche pendenti non restano senza via di ritorno
            Assert.Single(Directory.GetDirectories(PatchBackup.BackupsFolder(wc), "*-pre-merge"));

            f.Changes.WorkingCopyPath = other;   // cambia la working copy (altra solution): la copia dell'altra non deve mai servire
            Assert.Null(f.ChangesSnap.Merge);
            await f.Cmd("mergeUndo");
            Assert.Empty(f.Svn.PatchApplied);   // nessuna patch applicata a una working copy che non e' la sua
            Assert.Single(Directory.GetDirectories(PatchBackup.BackupsFolder(wc), "*-pre-merge"));   // e la copia resta dov'e'
        }
        finally
        {
            Directory.Delete(wc, true);
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public async Task Anteprima_FermataDaE155015_MostraComunqueIFileDiOgniRevisione_ConSpiegazione()
    {
        using var f = new Fixture();
        f.Svn.DryRunError = E155015;
        await f.OpenMergeAsync();

        await f.LogCmd("mergePreview");

        Assert.Contains("Anteprima incompleta", f.Merge.Error);
        Assert.Contains("r22", f.Merge.Error);
        Assert.Equal([4L, 5, 7], f.Merge.Result!.ByRev!.Select(g => g.Rev));
        Assert.Equal("preview", f.Merge.Result.Mode);
        Assert.DoesNotContain("E155015", f.Merge.Error);   // niente testo di svn in inglese
    }

    [Fact]
    public void Overlap_FileModificatiToccatiDalMerge_PercorsiNormalizzati()
    {
        var pending = new[] { new SvnFileStatus("src\\a.cs", SvnStatusKind.Modified), new SvnFileStatus("b.cs", SvnStatusKind.Added), new SvnFileStatus("z.cs", SvnStatusKind.Deleted) };
        var dry = MergeParser.ParseMerge("--- Merging r4 into '.':\nU    src/a.cs\nU    SRC/NUOVO.cs\nA    b.cs\n");

        Assert.Equal(["b.cs", "src/a.cs"], MergeStore.Overlap(pending, dry));
        Assert.Empty(MergeStore.Overlap([], dry));
    }

    [Fact]
    public async Task Anteprima_DryRun_ConLeRevisioniScelteOTutte_NonToccaLaWorkingCopy()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge(DryRun + "C    z.txt\n");
        await f.OpenMergeAsync();

        await f.LogCmd("mergePreview");
        await f.LogCmd("mergeToggle", new { rev = 5 });
        await f.LogCmd("mergeToggle", new { rev = 99 });   // non in elenco: ignorata
        await f.LogCmd("mergePreview");

        Assert.Equal(["- True", "5 True"], Fixture.Calls(f));
        Assert.Equal("preview", f.Merge.Result!.Mode);
        Assert.Equal("r5", f.Merge.Result.Revs);
        Assert.Equal(1, f.Merge.Result.Counts.Conflicted);
        Assert.Null(f.ChangesSnap.Merge);
        Assert.Equal("", f.ChangesSnap.Commit.Text);
    }

    [Fact]
    public async Task MergeTutto_DallaRadice_MessaggioNeiRecenti_NonNelCampo_BannerESenzaAutoStage()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge(DryRun);
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();

        await f.LogCmd("mergeAll");

        Assert.Equal(["4,5,7 True", "4,5,7 False"], Fixture.Calls(f));   // dry-run di controllo e poi il merge: tutte le revisioni adiacenti nell'elenco in un solo intervallo (4-7)
        Assert.Equal([(4L, 7L)], f.Svn.RangeMerges.Select(r => r.Ranges.Single()).Distinct());
        var c = f.ChangesSnap;
        Assert.Equal("", c.Commit.Text);   // il campo del messaggio non si tocca
        Assert.Equal(["Unite le revisioni 4-5,7 da branches/feature-x:\n........\nr4 | mrossi | feat: modifica a\n........\nr5 | lbianchi | feat: aggiunge new\ndettaglio\n........\nr7 | gverdi | feat: elimina b"], c.Commit.Recent);   // in cima ai Recenti
        Assert.Contains("annulla il merge", c.Merge!.Text);
        Assert.Contains("nei Recenti", c.Merge.Text);
        Assert.Equal(0, c.Merge.Conflicts);
        Assert.True(c.Commit.All);   // regola di sempre: Staged vuoto = Commit All, nulla finisce in Staged da solo
        Assert.Equal(0, c.Sections.Single(s => s.Id == "staged").Count);
        Assert.Equal("merge", f.Merge.Result!.Mode);
        Assert.Contains("rivedi", c.Status.Msg);
    }

    [Fact]
    public async Task MergeSelezionate_CherryPick_ElencaLeRevisioniNelMessaggio_ELeTogliDaDaUnire()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();
        await f.LogCmd("mergeSelected");   // niente scelta
        Assert.Contains("almeno una revisione", f.Merge.Error);
        Assert.Empty(f.Svn.Merges);

        await f.LogCmd("mergeToggle", new { rev = 7 });
        await f.LogCmd("mergeToggle", new { rev = 4 });
        Assert.Equal([4L, 7], f.Merge.Selected);
        f.Svn.EligibleAfterMerge = [5];
        await f.LogCmd("mergeSelected");

        Assert.Equal(["4 False", "7 False"], Fixture.Calls(f));   // la 5 (da unire ma non scelta) sta in mezzo: due passaggi, mai un intervallo che la comprenda
        Assert.Equal("", f.ChangesSnap.Commit.Text);
        Assert.Equal("Unite le revisioni 4,7 da branches/feature-x:\n........\nr4 | mrossi | feat: modifica a\n........\nr7 | gverdi | feat: elimina b", f.ChangesSnap.Commit.Recent![0]);
        Assert.Equal([5L], f.LogSnap.Entries.Select(e => e.Rev));
        Assert.Empty(f.Merge.Selected);
        Assert.Equal("", f.Merge.Error);
    }

    [Fact]
    public async Task Merge_ConConflitti_IlBannerAvvisa_ELaRigaCompareInChanges()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge("--- Merging r4 into '.':\nC    a.txt\nSummary of conflicts:\n  Text conflicts: 1\n");
        await f.OpenMergeAsync();
        f.Svn.OnMerge.Add(new SvnFileStatus(@"C:\wc\a.txt", SvnStatusKind.Conflicted, IsTreeConflict: false));

        await f.LogCmd("mergeAll");

        // Si e' fermato alla r4 (in conflitto): applicata, le altre due restano da unire e il banner dice cosa fare.
        var banner = f.ChangesSnap.Merge!;
        Assert.Equal(1, banner.Conflicts);
        Assert.NotNull(banner.Pending);
        Assert.Equal((1, 3, 2), (banner.Pending!.Applied, banner.Pending.Total, banner.Pending.Remaining));
        Assert.Equal(4L, banner.Pending.ConflictRev);
        Assert.Contains("ferma alla r4 per conflitto", banner.Pending.AppliedText);
        Assert.Contains("Rimaste 2", banner.Pending.RemainingText);
        Assert.False(banner.Pending.CanContinue);   // il conflitto e' ancora li'
        Assert.Contains("1 file in conflitto", banner.Pending.WhyNot);
        Assert.Contains("Continua merge", f.ChangesSnap.Status.Msg);
        Assert.Contains(f.ChangesSnap.Sections.SelectMany(s => s.Rows), r => r.Status == "C");
    }

    [Fact]
    public async Task Snapshot_LogInModalitaMergeEBanner_RispettanoGliSchemi()
    {
        using var f = new Fixture();
        f.Svn.MergeResult = MergeParser.ParseMerge(DryRun + "C    z.txt\nSummary of conflicts:\n  Text conflicts: 1\n");
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();
        Assert.Empty(SnapshotSchemaTests.Validate("log-state", UiJson.Serialize(f.Log.GetSnapshot())));

        await f.LogCmd("mergeToggle", new { rev = 5 });
        await f.LogCmd("mergePreview");
        Assert.Empty(SnapshotSchemaTests.Validate("log-state", UiJson.Serialize(f.Log.GetSnapshot())));
        await f.LogCmd("mergeAll");

        Assert.Empty(SnapshotSchemaTests.Validate("log-state", UiJson.Serialize(f.Log.GetSnapshot())));
        Assert.Empty(SnapshotSchemaTests.Validate("changes-state", UiJson.Serialize(f.Changes.GetSnapshot())));
        Assert.Contains("\"merge\"", UiJson.Serialize(f.Changes.GetSnapshot()));
        await f.LogCmd("progressClose", new { conflicts = false });
        await f.LogCmd("mergeClose");
        Assert.DoesNotContain("\"merge\"", UiJson.Serialize(f.Log.GetSnapshot()));   // log normale: il campo non c'e'
    }

    [Fact]
    public async Task Merge_ConUnMessaggioGiaScritto_NonLoSovrascrive()
    {
        using var f = new Fixture();
        f.Changes.CommitMessage.Text = "il mio messaggio";
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();

        await f.LogCmd("mergeAll");

        Assert.Equal("il mio messaggio", f.ChangesSnap.Commit.Text);
        Assert.NotNull(f.ChangesSnap.Merge);
    }

    [Fact]
    public async Task Merge_ErroreDiSvn_ResteInChiaro_NienteBannerNeMessaggio()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        f.Svn.MergeError = "svn: E195020: Cannot merge into mixed-revision working copy";

        await f.LogCmd("mergeAll");

        Assert.Contains("mixed-revision", f.Merge.Error);
        Assert.False(f.Merge.Busy);
        Assert.True(f.Merge.NeedsUpdate);   // la UI offre "SVN Update ora"
        Assert.Null(f.ChangesSnap.Merge);
        Assert.Equal("", f.ChangesSnap.Commit.Text);
        Assert.Empty(f.ChangesSnap.Commit.Recent!);
    }

    [Fact]
    public async Task Merge_RevisioniMiste_SvnUpdateOra_AggiornaERicalcolaLeRevisioni_SenzaDialoghi()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        f.Svn.MergeError = "svn: E195020: Cannot merge into mixed-revision working copy";
        await f.LogCmd("mergeAll");
        Assert.True(f.Merge.NeedsUpdate);
        f.Svn.MergeError = null;
        f.Svn.Eligible = [5, 7];   // dopo l'update il calcolo e' diverso

        await f.LogCmd("mergeUpdate");

        Assert.Equal(1, f.Svn.Updates);
        Assert.Equal("", f.Merge.Error);
        Assert.False(f.Merge.NeedsUpdate);
        Assert.Equal(2, f.Merge.Eligible);   // revisioni ricalcolate
        Assert.Empty(f.Dialogs.Asked);
        Assert.Empty(f.Svn.Merges);   // l'update non fa partire il merge
    }

    [Fact]
    public async Task ErroreCheSuggeriscUpdate_InChanges_OffreIlPulsanteUpdate()
    {
        using var f = new Fixture();
        f.Changes.WorkingCopyPath = @"C:\wc";
        await f.Changes.RefreshAsync(Ct);
        f.Svn.StatusError = "svn: E195020: Cannot merge into mixed-revision working copy";
        await f.Changes.RefreshAsync(Ct);

        var st = f.ChangesSnap.Status;
        Assert.True(st.Err);
        Assert.Equal("update", st.Action?.Cmd);
    }

    [Fact]
    public async Task MergeBloccato_SvnRevertDiTutto_ConfermaConElenco_NonTocca_INonVersionati_EPoiSiRiprova()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\a.txt", SvnStatusKind.Conflicted, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\sub\b.txt", SvnStatusKind.Missing, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\u.txt", SvnStatusKind.Unversioned, IsTreeConflict: false));
        await f.OpenMergeAsync();
        Assert.Contains("2 elementi", f.Merge.Blocked);

        f.Dialogs.ConfirmResult = false;   // conferma negata: niente
        await f.LogCmd("mergeRevertAll");
        Assert.Empty(f.Svn.Reverted);
        Assert.Contains("2 elementi", f.Merge.Blocked);

        f.Dialogs.ConfirmResult = true;
        await f.LogCmd("mergeRevertAll");

        var msg = f.Dialogs.Messages.Last();
        Assert.Contains("a.txt", msg);
        Assert.Contains("sub/b.txt", msg);
        Assert.DoesNotContain("u.txt", msg);
        var reverted = f.Svn.Reverted.SelectMany(r => r).ToList();
        Assert.Equal([@"C:\wc\a.txt", @"C:\wc\sub\b.txt"], reverted.Order());
        Assert.DoesNotContain(@"C:\wc\u.txt", reverted);
        Assert.Equal("", f.Merge.Blocked);   // avviso ricalcolato: il merge si puo' rifare
        await f.LogCmd("mergeAll");
        Assert.Single(f.Svn.Merges, m => !m.DryRun);
    }

    [Fact]
    public async Task MergeBloccato_VaiAChanges_PortaInPrimoPianoLaFinestra()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();

        await f.LogCmd("mergeResolve");

        Assert.Equal(1, f.Host.ChangesShown);
    }

    [Fact]
    public async Task BannerSparisce_ConNascondiECommit_EAnnullaMergeFaRevertRicorsivo()
    {
        using var f = new Fixture();
        await f.OpenMergeAsync();
        f.MergeLeavesChanges();
        await f.LogCmd("mergeAll");
        await f.Cmd("mergeDismiss");
        Assert.Null(f.ChangesSnap.Merge);
        f.Svn.Merges.Clear();
        f.Svn.Statuses.Clear();   // ripulita a mano: si puo' unire di nuovo
        f.Svn.Merges.Clear();
        await f.LogCmd("mergeAll");   // di nuovo
        Assert.NotNull(f.ChangesSnap.Merge);
        f.Dialogs.ConfirmResult = false;
        await f.Cmd("mergeUndo");   // conferma negata: non si tocca nulla
        Assert.Empty(f.Svn.Reverted);
        Assert.NotNull(f.ChangesSnap.Merge);

        f.Dialogs.ConfirmResult = true;
        f.Svn.Eligible = [4, 5, 6, 7];   // dopo l'annullamento le revisioni tornano da unire
        await f.Cmd("mergeUndo");

        Assert.Contains("a.txt", f.Dialogs.Messages.Last());   // la conferma elenca i file
        Assert.Equal([[@"C:\wc"]], f.Svn.Reverted);
        Assert.Equal([true], f.Svn.RevertRecursive);
        Assert.Null(f.ChangesSnap.Merge);
        Assert.Equal("", f.ChangesSnap.Commit.Text);
        Assert.Single(f.ChangesSnap.Commit.Recent!);   // il messaggio del merge resta nei Recenti anche dopo il revert
        Assert.Contains("Merge annullato", f.ChangesSnap.Status.Msg);
        Assert.Equal(4, f.Merge.Eligible);
    }

    [Fact]
    public async Task ChiudiMerge_TornaAlLogNormale_UnaNuovaWorkingCopyEsceDalMerge()
    {
        using var f = new Fixture();
        f.Svn.LogEntryCount = 3;
        await f.OpenMergeAsync();
        Assert.NotNull(f.LogSnap.Merge);

        await f.LogCmd("mergeClose");

        Assert.Null(f.LogSnap.Merge);
        Assert.Equal("Log: wc", f.Host.LogWindowTitles.Last());

        await f.OpenMergeAsync();
        await f.Log.ApplyAutoDetectedWorkingCopyAsync(@"C:\altra", Ct);
        Assert.Null(f.LogSnap.Merge);
    }

    // ── Prova con svn vero (repository file://) ──────────────────────

    /// <summary>trunk + branch con 4 revisioni (modifica, aggiunta, eliminazione, modifica + proprieta'), poi un conflitto forzato.</summary>
    [Fact]
    public async Task SvnVero_Eligible_DryRun_MergeTutto_CherryPick_Conflitto_Annulla()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_MgReal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string Run(string exe, string args, string cwd)
            {
                using var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var o = p.StandardOutput.ReadToEnd();
                var e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, $"{Path.GetFileName(exe)} {args}: {e}");
                return o;
            }

            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            Run(svnExe, $"mkdir -m init \"{url}/trunk\" \"{url}/branches\" \"{url}/tags\"", root);
            var wc = Path.Combine(root, "wc");
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc}\"", root);
            File.WriteAllText(Path.Combine(wc, "a.txt"), "line1\nline2\nline3\n");
            File.WriteAllText(Path.Combine(wc, "b.txt"), "bee\n");
            File.WriteAllText(Path.Combine(wc, "c.txt"), "cee\n");
            Directory.CreateDirectory(Path.Combine(wc, "sub"));
            Run(svnExe, "add a.txt b.txt c.txt sub", wc);
            Run(svnExe, "commit -m base", wc);                                                    // r2
            Run(svnExe, $"copy \"{url}/trunk\" \"{url}/branches/feature\" -m branch", root);     // r3
            var bwc = Path.Combine(root, "bwc");
            Run(svnExe, $"checkout \"{url}/branches/feature\" \"{bwc}\"", root);
            File.WriteAllText(Path.Combine(bwc, "a.txt"), "line1\nline2 BRANCH\nline3\n");
            Run(svnExe, "commit -m \"feat: modifica a\"", bwc);                                   // r4
            File.WriteAllText(Path.Combine(bwc, "new.txt"), "new\n");
            Run(svnExe, "add new.txt", bwc);
            Run(svnExe, "commit -m \"feat: aggiunge new\"", bwc);                                 // r5
            Run(svnExe, "rm b.txt", bwc);
            Run(svnExe, "commit -m \"feat: elimina b\"", bwc);                                    // r6
            File.WriteAllText(Path.Combine(bwc, "c.txt"), "cee BRANCH\n");
            Run(svnExe, "propset myprop 1 sub", bwc);
            Run(svnExe, "commit -m \"feat: c e proprieta su sub\"", bwc);                         // r7
            Run(svnExe, "update", wc);

            var svn = new SvnService(svnExe);
            var src = url + "/branches/feature";

            // revisioni da unire + log del sorgente
            var eligible = await svn.GetEligibleRevisionsAsync(wc, src, Ct);
            Assert.Equal([4L, 5, 6, 7], eligible);
            var log = await svn.GetLogRangeAsync(src, eligible[0], eligible[^1], Ct);
            Assert.Equal([7L, 6, 5, 4], log.Select(e => e.Revision));
            Assert.Equal("feat: modifica a", log[^1].Message);

            // anteprima di tutto: nessuna modifica alla working copy
            var dry = await svn.MergeAsync(wc, src, null, dryRun: true, Ct);
            Assert.Equal(["a.txt", "c.txt", "new.txt", "sub", "b.txt"], dry.Items.Select(i => i.Path));
            Assert.Equal(0, dry.Conflicts);
            Assert.Empty(await svn.GetStatusAsync(wc, Ct));

            // merge di tutto: le righe di mergeinfo non sono file, le revisioni non sono piu' da unire
            var all = await svn.MergeAsync(wc, src, null, dryRun: false, Ct);
            Assert.Equal(5, all.Items.Count);
            Assert.DoesNotContain(all.Items, i => i.Path == ".");
            Assert.Empty(await svn.GetEligibleRevisionsAsync(wc, src, Ct));
            Assert.NotEmpty(await svn.GetStatusAsync(wc, Ct));

            // annulla = revert ricorsivo della radice: la working copy torna pulita
            await svn.RevertAsync([wc], Ct, recursive: true);
            Assert.DoesNotContain(await svn.GetStatusAsync(wc, Ct), s => s.Status != SvnStatusKind.Unversioned);
            Assert.Equal([4L, 5, 6, 7], await svn.GetEligibleRevisionsAsync(wc, src, Ct));

            // cherry-pick di r4 e r6
            var pick = await svn.MergeAsync(wc, src, [4, 6], dryRun: false, Ct);
            Assert.Equal(["a.txt", "b.txt"], pick.Items.Select(i => i.Path).Order());
            Assert.Equal([5L, 7], await svn.GetEligibleRevisionsAsync(wc, src, Ct));
            await svn.RevertAsync([wc], Ct, recursive: true);

            // conflitto forzato: trunk modifica la stessa riga di a.txt
            File.WriteAllText(Path.Combine(wc, "a.txt"), "line1\nline2 TRUNK\nline3\n");
            Run(svnExe, "commit -m \"trunk: modifica a\"", wc);
            Run(svnExe, "update", wc);
            var preview = await svn.MergeAsync(wc, src, [4], dryRun: true, Ct);
            Assert.Equal(1, preview.Conflicts);
            var conflict = await svn.MergeAsync(wc, src, [4], dryRun: false, Ct);
            Assert.Equal(1, conflict.Conflicts);
            Assert.Equal("C", conflict.Items.Single().Action);
            Assert.Contains(await svn.GetStatusAsync(wc, Ct), s => s.Status == SvnStatusKind.Conflicted);

            // working copy a revisioni miste: svn rifiuta e il messaggio suggerisce Update
            await svn.RevertAsync([wc], Ct, recursive: true);
            foreach (var leftover in Directory.EnumerateFiles(wc, "a.txt.*"))
                File.Delete(leftover);
            File.WriteAllText(Path.Combine(wc, "sub", "d.txt"), "d\n");
            Run(svnExe, "add sub/d.txt", wc);
            Run(svnExe, "commit -m x", wc);
            Run(svnExe, "update -r 2 sub", wc);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svn.MergeAsync(wc, src, [5], dryRun: false, Ct));
            Assert.Contains("SVN Update", ex.Message);
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
