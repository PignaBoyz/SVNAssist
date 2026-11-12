using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>SVN Merge (0.3.0 fase 2): parser dell'output testuale, argomenti, selettore, Log in modalita' merge, banner e annulla; in fondo prova con svn vero su un repository file://.</summary>
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

    [Fact]
    public async Task Merge_WorkingCopySporca_Bloccata_ConSpiegazione_NienteMerge()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\x.cs", SvnStatusKind.Modified, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\nuovo.txt", SvnStatusKind.Unversioned, IsTreeConflict: false));   // i non versionati non contano
        await f.OpenMergeAsync();
        Assert.Contains("1 elementi con modifiche locali", f.Merge.Blocked);

        await f.LogCmd("mergeAll");

        Assert.Empty(f.Svn.Merges);
        Assert.Contains("Commit oppure Revert", f.Merge.Blocked);
        Assert.Null(f.ChangesSnap.Merge);

        f.Svn.Statuses.Clear();   // ripulita: il blocco cade al tentativo successivo
        await f.LogCmd("mergeAll");
        Assert.Single(f.Svn.Merges);
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

        Assert.Equal(["- False"], Fixture.Calls(f));
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

        Assert.Equal(["4,7 False"], Fixture.Calls(f));
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

        Assert.Equal(1, f.ChangesSnap.Merge!.Conflicts);
        Assert.Contains("1 file in conflitto", f.ChangesSnap.Status.Msg);
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
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\a.txt", SvnStatusKind.Modified, IsTreeConflict: false));
        f.Svn.Statuses.Add(new SvnFileStatus(@"C:\wc\sub\b.txt", SvnStatusKind.Added, IsTreeConflict: false));
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
        Assert.Single(f.Svn.Merges);
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
