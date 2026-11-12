using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// Sezione Conflitti (file di appoggio da svn info, azioni mia/loro/risolto/editor, proposta "Risolvi ora"), selettore Switch/Merge
/// per repository grandi (svn list con molte voci, ultimi usati) e ricerca dell'editor di merge. In fondo, prova con svn vero.
/// </summary>
public class ConflictsTests
{
    private const string Wc = @"C:\wc";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string FakeSvnPath = @"C:\fake\svn.exe";

    private sealed class Runner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Func<IReadOnlyList<string>, ProcessResult> Respond { get; set; } = _ => new ProcessResult(0, "", "");

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory,
            CancellationToken ct, string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(arguments);
            return Task.FromResult(Respond(arguments));
        }
    }

    // ── Parsing: svn info --xml dei conflitti, svn list --xml con molte voci ─────────────────────

    private const string InfoXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <info>
        <entry kind="file" path="C:\wc\README.txt" revision="5">
        <url>file:///repo/trunk/README.txt</url>
        <conflict operation="merge" type="text">
        <version kind="file" path-in-repos="trunk/README.txt" repos-url="file:///repo" revision="2" side="source-left"/>
        <prev-base-file>C:/wc/README.txt.merge-left.r2</prev-base-file>
        <prev-wc-file>C:/wc/README.txt.working</prev-wc-file>
        <cur-base-file>C:/wc/README.txt.merge-right.r5</cur-base-file>
        </conflict>
        </entry>
        <entry kind="file" path="C:\wc\Altro.txt" revision="5"><url>file:///repo/trunk/Altro.txt</url></entry>
        <entry kind="dir" path="C:\wc\sub" revision="5"><conflict operation="update" type="property"><prop-file>C:/wc/sub/dir_conflicts.prej</prop-file></conflict></entry>
        </info>
        """;

    [Fact]
    public void ParseConflictInfoXml_FileDiAppoggioDelMerge_TipoEPercorsiCompleti()
    {
        var info = SvnService.ParseConflictInfoXml(InfoXml);

        var c = info[@"C:\wc\README.txt"];
        Assert.Equal("text", c.Type);
        Assert.Equal(@"C:\wc\README.txt.merge-left.r2", c.Base);
        Assert.Equal(@"C:\wc\README.txt.working", c.Mine);
        Assert.Equal(@"C:\wc\README.txt.merge-right.r5", c.Theirs);
        Assert.Equal(3, c.Files.Count());
        Assert.DoesNotContain(@"C:\wc\Altro.txt", info.Keys);   // niente <conflict>: non compare
        var p = info[@"C:\wc\sub"];
        Assert.Equal("property", p.Type);
        Assert.Equal([@"C:\wc\sub\dir_conflicts.prej"], p.Files);
    }

    [Fact]
    public async Task GetConflictInfoAsync_UnaChiamataSvnInfoXml_ChiaviComeIPercorsiChiesti_ErroreDiSvnVuoto()
    {
        var runner = new Runner { Respond = _ => new ProcessResult(0, InfoXml, "") };
        var svn = new SvnService(FakeSvnPath, runner);

        var info = await svn.GetConflictInfoAsync([@"C:\wc\README.txt", @"C:\wc\Altro.txt"], Ct);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["info", "--non-interactive", "--xml", @"C:\wc\README.txt", @"C:\wc\Altro.txt"], call);
        Assert.Equal([@"C:\wc\README.txt"], info.Keys);
        Assert.Empty(await svn.GetConflictInfoAsync([], Ct));
        Assert.Single(runner.Calls);   // nessun target = nessuna chiamata

        runner.Respond = _ => new ProcessResult(1, "", "svn: E155010: The node was not found");
        Assert.Empty(await svn.GetConflictInfoAsync([@"C:\wc\README.txt"], Ct));   // un errore non ferma il refresh
    }

    [Fact]
    public void ParseListXml_ConRevisioneAutoreEData_EMoltiElementi()
    {
        var sb = new System.Text.StringBuilder("<lists><list path=\"https://x/repo/branches\">");
        for (var i = 500; i >= 1; i--)
            sb.Append($"<entry kind=\"dir\"><name>feature-{i:D3}</name><commit revision=\"{1000 + i}\"><author>u{i % 5}</author><date>2026-11-{1 + i % 28:D2}T10:00:00.000000Z</date></commit></entry>");
        sb.Append("<entry kind=\"file\"><name>LEGGIMI.txt</name></entry></list></lists>");

        var list = SvnService.ParseListXml(sb.ToString());

        Assert.Equal(500, list.Count);   // i file sono scartati
        Assert.Equal("feature-001", list[0].Name);   // in ordine alfabetico
        Assert.Equal("feature-500", list[^1].Name);
        Assert.Equal((1001L, "u1"), (list[0].Revision, list[0].Author));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), list[0].Date);
        Assert.All(list, e => Assert.True(e.Revision > 1000 && e.Author.StartsWith('u') && e.Date is not null));
    }

    [Fact]
    public void ParseListXml_SenzaCommit_RevisioneZeroEDataNulla()
    {
        var e = Assert.Single(SvnService.ParseListXml("<lists><list><entry kind=\"dir\"><name>a</name></entry></list></lists>"));
        Assert.Equal(("a", 0L, "", (DateTimeOffset?)null), (e.Name, e.Revision, e.Author, e.Date));
    }

    // ── Editor di merge ──────────────────────────────────────────────

    private static readonly MergeEditorFiles Files = new(@"C:\wc\a b.txt.r2", @"C:\wc\a b.txt.mine", @"C:\wc\a b.txt.r3", @"C:\wc\a b.txt");

    [Fact]
    public void MergeEditor_ArgomentiDiVsDiffMerge_TheirsMineBaseMergedEFlagMerge()
        => Assert.Equal([@"C:\wc\a b.txt.r3", @"C:\wc\a b.txt.mine", @"C:\wc\a b.txt.r2", @"C:\wc\a b.txt", "/m"], MergeEditor.VsArguments(Files));

    [Fact]
    public void MergeEditor_ArgomentiDiTortoiseMerge_ConPercorsiTraVirgolette()
    {
        var a = MergeEditor.TortoiseArguments(Files);

        Assert.StartsWith(@"/base:""C:\wc\a b.txt.r2"" /theirs:""C:\wc\a b.txt.r3"" /mine:""C:\wc\a b.txt.mine"" /merged:""C:\wc\a b.txt""", a);
        Assert.Contains(@"/mergedname:""a b.txt""", a);
    }

    [Fact]
    public void MergeEditor_TrovaVsDiffMergeNelVsOspite_PoiSottoProgramFiles_LaVersionePiuRecente()
    {
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_Vs_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string Make(string version, string edition)
            {
                var dir = Path.Combine(root, "Microsoft Visual Studio", version, edition, "Common7", "IDE", "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer");
                Directory.CreateDirectory(dir);
                var exe = Path.Combine(dir, MergeEditor.VsDiffMergeName);
                File.WriteAllText(exe, "");
                return exe;
            }

            Assert.Null(MergeEditor.FindVsDiffMerge(appIdDir: "", programFilesRoots: [root]));   // niente installato
            var vs2022 = Make("2022", "Community");
            Assert.Equal(vs2022, MergeEditor.FindVsDiffMerge("", [root]));
            var vs18 = Make("18", "Professional");
            Assert.Equal(vs18, MergeEditor.FindVsDiffMerge("", [root]));   // "18" (2026) e' piu' recente di "2022"

            // il VS che ospita (VSAPPIDDIR) ha la precedenza
            var hostIde = Path.Combine(root, "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE");
            Assert.Equal(vs2022, MergeEditor.FindVsDiffMerge(hostIde, [root]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Store: sezione Conflitti ─────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public StubSettings Settings { get; } = new();
        public FakeHost Host { get; } = new();
        public ChangesStore Store { get; }
        public List<MergeEditorFiles> Launched { get; } = [];
        public string? LaunchResult { get; set; } = "vsDiffMerge";

        public Fixture()
        {
            Store = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())), recentTargetsFile: TempFile())
            { WorkingCopyPath = Wc, TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Cf_" + Guid.NewGuid().ToString("N")[..8]) };
            Store.MergeEditorLauncher = f => { Launched.Add(f); return LaunchResult; };
        }

        public ChangesState Snap => (ChangesState)Store.GetSnapshot();
        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
        public void Dispose() => Store.Dispose();

        /// <summary>README.txt in conflitto di testo con i tre file di appoggio del merge, che svn status elenca anche come non versionati.</summary>
        public void ConflictWithAuxFiles()
        {
            Svn.Statuses.Add(new SvnFileStatus("README.txt", SvnStatusKind.Conflicted));
            foreach (var aux in new[] { "README.txt.merge-left.r2", "README.txt.merge-right.r5", "README.txt.working" })
                Svn.Statuses.Add(new SvnFileStatus(aux, SvnStatusKind.Unversioned));
            Svn.Statuses.Add(new SvnFileStatus("vero-non-versionato.txt", SvnStatusKind.Unversioned));
            Svn.ConflictInfos[Path.Combine(Wc, "README.txt")] = new SvnConflictInfo("text",
                Path.Combine(Wc, "README.txt.merge-left.r2"), Path.Combine(Wc, "README.txt.working"), Path.Combine(Wc, "README.txt.merge-right.r5"), null);
        }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "SVNAssist_Recent_" + Guid.NewGuid().ToString("N")[..8] + ".json");

    [Fact]
    public async Task Conflitti_FileDiAppoggioRaggruppatiSottoIlFile_NonTraINonVersionati()
    {
        using var f = new Fixture();
        f.ConflictWithAuxFiles();

        await f.Store.RefreshAsync(Ct);

        var c = Assert.Single(f.Snap.Conflicts!);
        Assert.Equal(("changes:README.txt", "README.txt", "text"), (c.Key, c.Name, c.Kind));
        Assert.Equal(["README.txt.merge-left.r2", "README.txt.merge-right.r5", "README.txt.working"], c.Files.Order());
        var unversioned = f.Snap.Sections.Single(s => s.Id == "unversioned");
        Assert.Equal(["vero-non-versionato.txt"], unversioned.Rows.Where(r => r.Kind == "file").Select(r => r.Name));   // gli ausiliari non ci sono
        Assert.Equal(2, f.Snap.Sections.Sum(s => s.Count));   // il conflitto + il vero non versionato
    }

    [Fact]
    public async Task Conflitti_SenzaDatiDaSvnInfo_NonSiIndovinaDalNome()
    {
        using var f = new Fixture();
        f.ConflictWithAuxFiles();
        f.Svn.ConflictInfos.Clear();

        await f.Store.RefreshAsync(Ct);

        Assert.Empty(Assert.Single(f.Snap.Conflicts!).Files);
        Assert.Equal(4, f.Snap.Sections.Single(s => s.Id == "unversioned").Rows.Count(r => r.Kind == "file"));   // restano dove sono
    }

    [Fact]
    public async Task Conflitti_TipoAdAlberoEProprieta()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("t.cs", SvnStatusKind.Conflicted, IsTreeConflict: true));
        f.Svn.Statuses.Add(new SvnFileStatus("p.cs", SvnStatusKind.Conflicted));
        f.Svn.ConflictInfos[Path.Combine(Wc, "p.cs")] = new SvnConflictInfo("property", null, null, null, Path.Combine(Wc, "p.cs.prej"));

        await f.Store.RefreshAsync(Ct);

        Assert.Equal([("p.cs", "property", new[] { "p.cs.prej" }), ("t.cs", "tree", Array.Empty<string>())],
            f.Snap.Conflicts!.OrderBy(c => c.Name).Select(c => (c.Name, c.Kind, c.Files.ToArray())));
    }

    [Fact]
    public async Task ConflictOp_Resolved_UsaWorking_Mine_Theirs_ChiedonoConferma()
    {
        using var f = new Fixture();
        f.ConflictWithAuxFiles();
        await f.Store.RefreshAsync(Ct);

        await f.Cmd("conflictOp", new { op = "resolved", key = "changes:README.txt" });
        await f.Cmd("conflictOp", new { op = "mine", key = "changes:README.txt" });
        await f.Cmd("conflictOp", new { op = "theirs", key = "changes:README.txt" });

        var full = Path.Combine(Wc, "README.txt");
        Assert.Equal([(full, SvnConflictChoice.Working), (full, SvnConflictChoice.MineFull), (full, SvnConflictChoice.TheirsFull)], f.Svn.Resolved);
        Assert.Equal(2, f.Dialogs.Messages.Count);   // conferma solo per mine e theirs: scartano una delle due parti
        Assert.Contains("TUA", f.Dialogs.Messages[0]);
        Assert.Contains("LORO", f.Dialogs.Messages[1]);

        f.Dialogs.ConfirmResult = false;
        await f.Cmd("conflictOp", new { op = "mine", key = "changes:README.txt" });
        Assert.Equal(3, f.Svn.Resolved.Count);   // annullato: niente resolve
    }

    [Fact]
    public async Task ConflictOp_ConflittoAdAlbero_SoloResolvedConConferma()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("t.cs", SvnStatusKind.Conflicted, IsTreeConflict: true));
        await f.Store.RefreshAsync(Ct);

        await f.Cmd("conflictOp", new { op = "mine", key = "changes:t.cs" });
        await f.Cmd("conflictOp", new { op = "theirs", key = "changes:t.cs" });
        await f.Cmd("conflictOp", new { op = "edit", key = "changes:t.cs" });
        Assert.Empty(f.Svn.Resolved);
        Assert.Empty(f.Dialogs.Messages);
        Assert.Contains("albero", f.Snap.Status.Msg);

        await f.Cmd("conflictOp", new { op = "resolved", key = "changes:t.cs" });

        Assert.Equal(SvnConflictChoice.Working, Assert.Single(f.Svn.Resolved).Choice);
        Assert.Contains("albero", Assert.Single(f.Dialogs.Messages));
    }

    [Fact]
    public async Task ConflictOp_Edit_AvviaLEditorConBaseMiaLoroEFileDaRisolvere()
    {
        using var f = new Fixture();
        f.ConflictWithAuxFiles();
        await f.Store.RefreshAsync(Ct);

        await f.Cmd("conflictOp", new { op = "edit", key = "changes:README.txt" });

        var launched = Assert.Single(f.Launched);
        Assert.Equal(new MergeEditorFiles(Path.Combine(Wc, "README.txt.merge-left.r2"), Path.Combine(Wc, "README.txt.working"),
            Path.Combine(Wc, "README.txt.merge-right.r5"), Path.Combine(Wc, "README.txt")), launched);
        Assert.Contains("vsDiffMerge", f.Snap.Status.Msg);
        Assert.Empty(f.Host.OpenedFiles);
        Assert.Empty(f.Svn.Resolved);   // l'editor non risolve: serve "Segna come risolto"
    }

    [Fact]
    public async Task ConflictOp_Edit_SenzaEditorOSenzaDati_ApreIlFileConIMarcatoriInVs()
    {
        using var f = new Fixture();
        f.ConflictWithAuxFiles();
        f.LaunchResult = null;   // nessun editor a 3 vie installato
        await f.Store.RefreshAsync(Ct);

        await f.Cmd("conflictOp", new { op = "edit", key = "changes:README.txt" });

        Assert.Equal([Path.Combine(Wc, "README.txt")], f.Host.OpenedFiles);
        Assert.Contains("marcatori", f.Snap.Status.Msg);

        f.Svn.ConflictInfos.Clear();   // senza i dati di svn info non si sa quale e' la base
        await f.Store.RefreshAsync(Ct);
        f.LaunchResult = "vsDiffMerge";
        await f.Cmd("conflictOp", new { op = "edit", key = "changes:README.txt" });
        Assert.Empty(f.Launched.Where(l => l.Base.Length == 0));
        Assert.Equal(2, f.Host.OpenedFiles.Count);
    }

    [Fact]
    public async Task ConflictOp_ChiaveNonDiUnConflitto_Errore()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("m.cs", SvnStatusKind.Modified));
        await f.Store.RefreshAsync(Ct);

        await f.Cmd("conflictOp", new { op = "mine", key = "changes:m.cs" });

        Assert.Empty(f.Svn.Resolved);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("conflictOp", new { op = "mine", key = "changes:non-esiste.cs" }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("conflictOp", new { op = "cancella", key = "changes:m.cs" }));
    }

    [Fact]
    public async Task Update_ConConflitti_ProponeRisolviOraOPiuTardi()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("a.cs", SvnStatusKind.Conflicted));
        f.Svn.UpdateConflicts = ["a.cs"];

        f.Dialogs.AskResult = false;   // "Più tardi": resta com'e'
        await f.Cmd("update");
        Assert.Contains("1 file in conflitto", Assert.Single(f.Dialogs.Asked));
        Assert.Equal(0, f.Host.ChangesShown);
        Assert.Contains("in conflitto", f.Snap.Status.Msg);

        f.Dialogs.AskResult = true;   // "Risolvi ora": porta in primo piano Changes
        await f.Cmd("update");
        Assert.Equal(1, f.Host.ChangesShown);
        Assert.Contains("sezione Conflitti", f.Snap.Status.Msg);
    }

    [Fact]
    public async Task Update_SenzaConflitti_NonChiedeNulla()
    {
        using var f = new Fixture();

        await f.Cmd("update");

        Assert.Empty(f.Dialogs.Asked);
    }

    [Fact]
    public async Task BannerDelMerge_ContaIConflittiRimastiAdOgniStato()
    {
        using var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("a.cs", SvnStatusKind.Conflicted));
        f.Svn.Statuses.Add(new SvnFileStatus("b.cs", SvnStatusKind.Conflicted));
        f.Svn.Statuses.Add(new SvnFileStatus("c.cs", SvnStatusKind.Modified));
        await f.Store.RefreshAsync(Ct);
        f.Store.AfterMerge("Merge r4-r6 da ^/branches/x", "Merge r4-r6 da ^/branches/x: rivedi le modifiche e committa, oppure annulla il merge.");

        Assert.Equal(2, f.Snap.Merge!.Conflicts);
        Assert.True(f.Store.MergePending);

        f.Svn.Statuses.RemoveAll(s => s.Path == "a.cs");   // risolto altrove
        await f.Store.RefreshAsync(Ct);
        Assert.Equal(1, f.Snap.Merge!.Conflicts);
    }

    // ── Selettore: ultimi usati e repository grandi ──────────────────

    private sealed class PickerFixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public StubSettings Settings { get; } = new();
        public string RecentFile { get; } = TempFile();
        public ChangesStore Store { get; }

        public PickerFixture(int branches = 500)
        {
            Svn.InfoRelativeUrl = "^/trunk";
            Svn.Entries["file:///repo"] = [new("branches", 900, "u", null), new("tags", 800, "u", null), new("trunk", 950, "mrossi", new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero))];
            Svn.Entries["file:///repo/branches"] = Enumerable.Range(1, branches).Select(i => new SvnListEntry($"feature-{i:D3}", 100 + i, i % 2 == 0 ? "mrossi" : "lbianchi", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i))).ToArray();
            Svn.Entries["file:///repo/tags"] = [new("1.0", 50, "mrossi", new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero))];
            Store = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())), recentTargetsFile: RecentFile)
            { WorkingCopyPath = Wc, TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Pk_" + Guid.NewGuid().ToString("N")[..8]) };
        }

        public SwitchPickerDto Picker => ((ChangesState)Store.GetSnapshot()).SwitchPicker;
        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public void Dispose()
        {
            Store.Dispose();
            if (File.Exists(RecentFile))
                File.Delete(RecentFile);
        }
    }

    [Fact]
    public async Task Selettore_500Branch_RevisioneAutoreDataEOrdineDelServer()
    {
        using var f = new PickerFixture(500);

        await f.Cmd("switchOpen");

        Assert.Equal(1 + 500 + 1, f.Picker.Items.Count);   // trunk + 500 branch + 1 tag
        Assert.Equal("trunk", f.Picker.Items[0].Name);
        Assert.Equal("branches/feature-001", f.Picker.Items[1].Name);
        var b = f.Picker.Items[2];
        Assert.Equal(("branches/feature-002", "branch", 102L, "mrossi"), (b.Name, b.Kind, b.Rev, b.Author));
        Assert.StartsWith("2026-01-03T00:00:00", b.Date);
        Assert.Equal(("tags/1.0", "tag"), (f.Picker.Items[^1].Name, f.Picker.Items[^1].Kind));
        Assert.True(f.Picker.Items[0].Current);
        Assert.Contains("\"rev\":950", System.Text.Json.JsonSerializer.Serialize(f.Picker.Items[0], UiJson.Options));
    }

    [Fact]
    public async Task Selettore_CaricaBranchETagInParallelo_ConUnaListaPerCartella()
    {
        using var f = new PickerFixture(5);

        await f.Cmd("switchOpen");

        Assert.Equal(["file:///repo", "file:///repo/branches", "file:///repo/tags"], f.Svn.Listed.Order());
    }

    [Fact]
    public async Task UltimiUsati_PerRepository_DalPiuRecente_SenzaDoppioni_MassimoOtto_SopravvivonoAlRiavvio()
    {
        using var f = new PickerFixture(20);
        await f.Cmd("switchOpen");
        Assert.Empty(f.Picker.Recent);

        // merge: la scelta della sorgente si ricorda anche se non si fa lo switch
        await f.Cmd("mergeOpen");
        await f.Cmd("switchOk", new { name = "branches/feature-003" });
        await f.Cmd("switchOpen");
        Assert.Equal(["branches/feature-003"], f.Picker.Recent);

        foreach (var n in new[] { 4, 5, 6, 7, 8, 9, 10, 11, 3 })
        {
            await f.Cmd("switchOpen");
            await f.Cmd("switchOk", new { name = $"branches/feature-{n:D3}" });
            f.Store.Switch.Close();
        }

        await f.Cmd("switchOpen");
        Assert.Equal(SwitchPickerStore.MaxRecent, f.Picker.Recent.Count);
        Assert.Equal("branches/feature-003", f.Picker.Recent[0]);   // l'ultimo usato in cima, senza doppioni
        Assert.Equal(f.Picker.Recent.Count, f.Picker.Recent.Distinct().Count());
        Assert.DoesNotContain("branches/feature-004", f.Picker.Recent);   // il piu' vecchio e' uscito

        // nuova sessione: stesso file
        var again = new SwitchPickerStore(f.Svn, f.Settings, f.RecentFile);
        await again.OpenAsync(Wc, Ct);
        Assert.Equal(f.Picker.Recent, again.GetSnapshot().Recent);

        // un altro repository ha i suoi
        f.Svn.RepoRoot = "file:///altro";
        f.Svn.Entries["file:///altro"] = [new("trunk", 1, "u", null)];
        var other = new SwitchPickerStore(f.Svn, f.Settings, f.RecentFile);
        await other.OpenAsync(Wc, Ct);
        Assert.Empty(other.GetSnapshot().Recent);
    }

    [Fact]
    public async Task UltimiUsati_FileRovinato_SiRipartiDaZero_ENomiNonPiuInElencoNonCompaiono()
    {
        using var f = new PickerFixture(3);
        File.WriteAllText(f.RecentFile, "{ non e json");
        await f.Cmd("switchOpen");
        Assert.Empty(f.Picker.Recent);

        File.WriteAllText(f.RecentFile, "{\"file:///repo\":[\"branches/cancellato\",\"branches/feature-002\"]}");
        await f.Cmd("switchRefresh");
        Assert.Equal(["branches/feature-002"], f.Picker.Recent);
    }

    // ── Merge: "da committare" nel Log e proposta dei conflitti ──────

    [Fact]
    public async Task MergeConConflitti_PropostaRisolviOra_LogPending_ResolveMostraChanges()
    {
        var svn = new FakeSvnService { InfoRelativeUrl = "^/trunk", Eligible = [4, 5] };
        svn.Folders["file:///repo"] = ["branches", "tags", "trunk"];
        svn.Folders["file:///repo/branches"] = ["feature-x"];
        svn.RangeLog = [new SvnLogEntry(4, "u", DateTimeOffset.UnixEpoch, "m", []), new SvnLogEntry(5, "u", DateTimeOffset.UnixEpoch, "m", [])];
        svn.OnMerge.Add(new SvnFileStatus(Path.Combine(Wc, "a.txt"), SvnStatusKind.Conflicted));
        svn.MergeResult = new MergeOutput([new MergeItem('C', ' ', false, "a.txt")], 1, 0);
        svn.EligibleAfterMerge = [];
        var dialogs = new FakeDialogs { AskResult = true };
        var host = new FakeHost();
        var settings = new StubSettings();
        using var changes = new ChangesStore(settings, svn, new FakeAiFactory(), new NoopAiPrompt(), dialogs, host,
            new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), recentTargetsFile: TempFile())
        { WorkingCopyPath = Wc, TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Mc_" + Guid.NewGuid().ToString("N")[..8]) };
        var log = new LogStore(svn, settings, host, null, changes);
        changes.StartMerge = log.StartMergeAsync;
        await changes.DispatchAsync("mergeOpen", JsonSerializer.SerializeToElement(new { }), Ct);
        await changes.DispatchAsync("switchOk", JsonSerializer.SerializeToElement(new { name = "branches/feature-x" }), Ct);
        Assert.False(((LogState)log.GetSnapshot()).Merge!.Pending);

        await log.DispatchAsync("mergeAll", JsonSerializer.SerializeToElement(new { }), Ct);

        var m = ((LogState)log.GetSnapshot()).Merge!;
        Assert.Equal(0, m.Eligible);   // le eligible sono ricalcolate: zero
        Assert.True(m.Pending);   // ma il merge e' da committare: la UI non dice "0 da unire"
        Assert.Equal(1, m.Result!.Counts.Conflicted);
        Assert.Contains("da committare", ((LogState)log.GetSnapshot()).Msg);
        Assert.Contains("Conflitti da risolvere in Changes: 1", ((LogState)log.GetSnapshot()).Msg);
        Assert.Contains("1 file in conflitto", Assert.Single(dialogs.Asked));
        Assert.Equal(1, host.ChangesShown);   // "Risolvi ora"

        await log.DispatchAsync("mergeResolve", JsonSerializer.SerializeToElement(new { }), Ct);
        Assert.Equal(2, host.ChangesShown);   // il pulsante "Risolvi i conflitti" del Log

        svn.Statuses.Clear();   // committato o annullato altrove: non e' piu' da committare
        await changes.RefreshAsync(Ct);
        Assert.False(((LogState)log.GetSnapshot()).Merge!.Pending);
    }

    // ── Prova con svn vero ───────────────────────────────────────────

    private static string Run(string exe, string args, string cwd)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{Path.GetFileName(exe)} {args}: {e}");
        return o;
    }

    /// <summary>
    /// Quattro conflitti veri da un merge (file f1..f4), poi le quattro azioni della sezione Conflitti: mia, loro, segna come risolto
    /// (prima rifiutato con i marcatori) ed editor di merge (avviato con i file giusti, tutti esistenti).
    /// </summary>
    [Fact]
    public async Task SvnVero_ConflittiDaMerge_AusiliariNascosti_QuattroAzioniDiResolve()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_CfReal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            Run(svnExe, $"mkdir -m init \"{url}/trunk\" \"{url}/branches\" \"{url}/tags\"", root);
            var wc = Path.Combine(root, "wc");
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc}\"", root);
            var names = new[] { "f1.txt", "f2.txt", "f3.txt", "f4.txt" };
            foreach (var n in names)
                File.WriteAllText(Path.Combine(wc, n), "a\nb\nc\n");
            Run(svnExe, "add " + string.Join(' ', names), wc);
            Run(svnExe, "commit -m base", wc);
            Run(svnExe, $"copy \"{url}/trunk\" \"{url}/branches/feat\" -m branch", root);
            var bwc = Path.Combine(root, "bwc");
            Run(svnExe, $"checkout \"{url}/branches/feat\" \"{bwc}\"", root);
            foreach (var n in names)
                File.WriteAllText(Path.Combine(bwc, n), "a\nB-BRANCH\nc\n");
            Run(svnExe, "commit -m \"feat: tocca tutto\"", bwc);
            foreach (var n in names)
                File.WriteAllText(Path.Combine(wc, n), "a\nB-TRUNK\nc\n");
            Run(svnExe, "commit -m \"trunk: tocca tutto\"", wc);
            Run(svnExe, "update", wc);

            var svn = new SvnService(svnExe);
            var merge = await svn.MergeAsync(wc, url + "/branches/feat", null, dryRun: false, Ct);
            Assert.Equal(4, merge.Conflicts);

            var info = await svn.GetConflictInfoAsync(names.Select(n => Path.Combine(wc, n)).ToList(), Ct);
            Assert.Equal(4, info.Count);
            var f1 = info[Path.Combine(wc, "f1.txt")];
            Assert.Equal("text", f1.Type);
            Assert.EndsWith("f1.txt.merge-left.r2", f1.Base);
            Assert.EndsWith("f1.txt.working", f1.Mine);
            Assert.EndsWith("f1.txt.merge-right.r5", f1.Theirs);
            Assert.All(f1.Files, p => Assert.True(File.Exists(p), p));

            var settings = new StubSettings();
            var dialogs = new FakeDialogs();
            var host = new FakeHost();
            var launched = new List<MergeEditorFiles>();
            using var store = new ChangesStore(settings, svn, new FakeAiFactory(), new NoopAiPrompt(), dialogs, host,
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), recentTargetsFile: TempFile())
            { WorkingCopyPath = wc, TempRoot = Path.Combine(root, "tmp"), MergeEditorLauncher = f => { launched.Add(f); return "finto"; } };
            Task Cmd(string name, object a) => store.DispatchAsync(name, JsonSerializer.SerializeToElement(a), Ct);
            ChangesState Snap() => (ChangesState)store.GetSnapshot();

            await store.RefreshAsync(Ct);

            Assert.Equal(4, Snap().Conflicts!.Count);
            Assert.All(Snap().Conflicts!, c => Assert.Equal(3, c.Files.Count));
            Assert.Equal(0, Snap().Sections.Single(s => s.Id == "unversioned").Count);   // dodici file di appoggio, nessuno tra i non versionati
            Assert.Equal(["f1.txt.merge-left.r2", "f1.txt.merge-right.r5", "f1.txt.working"], Snap().Conflicts!.Single(c => c.Name == "f1.txt").Files.Order());

            // "Usa la mia" = la versione della working copy (trunk)
            dialogs.ConfirmResult = true;
            await Cmd("conflictOp", new { op = "mine", key = "changes:f1.txt" });
            Assert.Equal("a\nB-TRUNK\nc\n", File.ReadAllText(Path.Combine(wc, "f1.txt")).Replace("\r\n", "\n"));
            // "Usa la loro" = la versione del branch
            await Cmd("conflictOp", new { op = "theirs", key = "changes:f2.txt" });
            Assert.Equal("a\nB-BRANCH\nc\n", File.ReadAllText(Path.Combine(wc, "f2.txt")).Replace("\r\n", "\n"));
            Assert.Equal(2, Snap().Conflicts!.Count);
            Assert.False(File.Exists(Path.Combine(wc, "f1.txt.working")));   // gli ausiliari spariscono dopo il resolve

            // "Segna come risolto" con i marcatori ancora nel file: rifiutato
            await Cmd("conflictOp", new { op = "resolved", key = "changes:f3.txt" });
            Assert.Contains("marcatori", Snap().Status.Msg);
            Assert.Equal(2, Snap().Conflicts!.Count);
            File.WriteAllText(Path.Combine(wc, "f3.txt"), "a\nB-TRUNK + B-BRANCH\nc\n");
            await Cmd("conflictOp", new { op = "resolved", key = "changes:f3.txt" });
            Assert.Equal(["f4.txt"], Snap().Conflicts!.Select(c => c.Name));

            // editor di merge: i quattro file giusti, tutti esistenti
            await Cmd("conflictOp", new { op = "edit", key = "changes:f4.txt" });
            var l = Assert.Single(launched);
            Assert.EndsWith("f4.txt.merge-left.r2", l.Base);
            Assert.EndsWith("f4.txt.working", l.Mine);
            Assert.EndsWith("f4.txt.merge-right.r5", l.Theirs);
            Assert.Equal(Path.Combine(wc, "f4.txt"), l.Merged);
            Assert.All(new[] { l.Base, l.Mine, l.Theirs, l.Merged }, p => Assert.True(File.Exists(p), p));
            Assert.Contains("<<<<<<<", File.ReadAllText(l.Merged));   // il file da risolvere ha i marcatori
            Assert.Equal("a\nB-BRANCH\nc\n", File.ReadAllText(l.Theirs).Replace("\r\n", "\n"));
            Assert.Equal("a\nB-TRUNK\nc\n", File.ReadAllText(l.Mine).Replace("\r\n", "\n"));
            Assert.Equal("a\nb\nc\n", File.ReadAllText(l.Base).Replace("\r\n", "\n"));

            File.WriteAllText(l.Merged, "a\nrisolto nell'editor\nc\n");
            await Cmd("conflictOp", new { op = "resolved", key = "changes:f4.txt" });
            Assert.Empty(Snap().Conflicts!);
            Assert.Contains("Conflitto risolto", Snap().Status.Msg);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* idem */ }
        }
    }

    /// <summary>Conflitto da update: i file di appoggio sono .mine, .rVECCHIA, .rNUOVA.</summary>
    [Fact]
    public async Task SvnVero_ConflittoDaUpdate_MineBaseTheirs()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_CfUpd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            Run(svnExe, $"mkdir -m init \"{url}/trunk\"", root);
            var wc1 = Path.Combine(root, "wc1");
            var wc2 = Path.Combine(root, "wc2");
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc1}\"", root);
            File.WriteAllText(Path.Combine(wc1, "x.txt"), "a\nb\nc\n");
            Run(svnExe, "add x.txt", wc1);
            Run(svnExe, "commit -m base", wc1);
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc2}\"", root);
            File.WriteAllText(Path.Combine(wc1, "x.txt"), "a\nSERVER\nc\n");
            Run(svnExe, "commit -m server", wc1);
            File.WriteAllText(Path.Combine(wc2, "x.txt"), "a\nLOCALE\nc\n");

            var svn = new SvnService(svnExe);
            var update = await svn.UpdateAsync(wc2, Ct);
            Assert.Single(update.ConflictedPaths);

            var info = (await svn.GetConflictInfoAsync([Path.Combine(wc2, "x.txt")], Ct)).Single().Value;
            Assert.EndsWith("x.txt.r2", info.Base);
            Assert.EndsWith("x.txt.mine", info.Mine);
            Assert.EndsWith("x.txt.r3", info.Theirs);
            Assert.Equal("a\nLOCALE\nc\n", File.ReadAllText(info.Mine).Replace("\r\n", "\n"));
            Assert.Equal("a\nSERVER\nc\n", File.ReadAllText(info.Theirs).Replace("\r\n", "\n"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* idem */ }
        }
    }
}
