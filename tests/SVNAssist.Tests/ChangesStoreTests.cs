using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Store della vista Changes con servizi finti (nessun svn, nessuna VS): stato, staged, comandi, chiavi di riga.</summary>
public class ChangesStoreTests
{
    private const string Wc = @"C:\wc";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeAiFactory Ai { get; } = new();
        public StubSettings Settings { get; } = new();
        public FakeHost Host { get; } = new();
        public ChangesStore Store { get; }
        public List<string> Recycled { get; } = [];
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "SVNAssist_Store_" + Guid.NewGuid().ToString("N")[..8]);

        public Fixture(string workingCopy = Wc)
        {
            Store = new ChangesStore(
                Settings, Svn, Ai, new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())))
            {
                WorkingCopyPath = workingCopy,
                TempRoot = TempRoot,
            };
            Store.DeleteFromDisk = Recycled.Add;
        }

        public Fixture With(params (string Path, SvnStatusKind Status)[] items)
        {
            foreach (var (path, status) in items)
                Svn.Statuses.Add(new SvnFileStatus(path, status, IsTreeConflict: false));
            return this;
        }

        /// <summary>Refresh + M/A/D/R messi in Staged esplicitamente (come farebbe l'utente): i test sullo staging partono da qui.</summary>
        public async Task Refresh()
        {
            await Store.RefreshAsync(Ct);
            foreach (var row in Section("changes").Rows.Where(r => r.Kind == "file" && (r.Status is "M" or "A" or "D" or "R")))
                await Cmd("stage", new { key = row.Key });
        }

        /// <summary>Solo il refresh: nulla viene messo in Staged da solo.</summary>
        public Task RefreshOnly() => Store.RefreshAsync(Ct);

        public ChangesState Snap => (ChangesState)Store.GetSnapshot();

        public SectionDto Section(string id) => Snap.Sections.Single(s => s.Id == id);

        public IEnumerable<string> Names(string section) => Section(section).Rows.Select(r => r.Name);

        public IEnumerable<string> FileKeys(string section) => Section(section).Rows.Where(r => r.Kind == "file").Select(r => r.Key);

        public RowDto Row(string key) => Snap.Sections.SelectMany(s => s.Rows).Single(r => r.Key == key);

        public Task Cmd(string name, object? args = null)
            => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public string Status => Snap.Status.Msg;

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
    }

    private static string Full(string relative) => Path.Combine(Wc, relative);

    // ── Stato e sezioni ──────────────────────────────────────────────

    [Fact]
    public async Task Refresh_CostruisceSezioniAlberoECompattaLeCartelle()
    {
        var f = new Fixture().With(
            ("src/Core/Services/A.cs", SvnStatusKind.Modified),
            ("src/Core/Services/B.cs", SvnStatusKind.Modified),
            ("src/Program.cs", SvnStatusKind.Added),
            ("notes.txt", SvnStatusKind.Unversioned),
            ("src/Conf.cs", SvnStatusKind.Conflicted));

        await f.Refresh();

        Assert.Equal(["staged", "changes", "unversioned"], f.Snap.Sections.Select(s => s.Id));
        Assert.Equal(["src", "Core/Services", "A.cs", "B.cs", "Program.cs"], f.Names("staged"));
        Assert.Equal(["src", "Conf.cs"], f.Names("changes"));
        Assert.Equal(["notes.txt"], f.Names("unversioned"));
        Assert.Equal([3, 1, 1], f.Snap.Sections.Select(s => s.Count));
        Assert.Equal("trunk", f.Snap.WorkingCopy.Branch);
        Assert.Equal("staged:src/Core/Services", f.Section("staged").Rows[1].Key);
        Assert.Equal("changes:src/Conf.cs", f.Section("changes").Rows[1].Key);
    }

    [Fact]
    public async Task Refresh_NienteInStagedDaSolo_TuttoInModificheENonVersionati()
    {
        var f = new Fixture().With(
            ("m", SvnStatusKind.Modified), ("a", SvnStatusKind.Added), ("d", SvnStatusKind.Deleted),
            ("r", SvnStatusKind.Replaced), ("x", SvnStatusKind.Missing),
            ("u", SvnStatusKind.Unversioned), ("c", SvnStatusKind.Conflicted));

        await f.RefreshOnly();

        Assert.Empty(f.Names("staged")); // come Git: Staged contiene solo cio' che l'utente ci mette
        Assert.Equal(["a", "c", "d", "m", "r", "x"], f.Names("changes"));
        Assert.Equal(["u"], f.Names("unversioned"));
        Assert.True(f.Snap.Commit.All);
        Assert.Equal(6, f.Snap.Commit.Count); // Commit All: tutte le modifiche versionate (u escluso)

        await f.Cmd("stage", new { key = "changes:m" });
        await f.RefreshOnly(); // la scelta dell'utente resta tra un refresh e l'altro
        Assert.Equal(["m"], f.Names("staged"));
        Assert.False(f.Snap.Commit.All);
    }

    [Fact]
    public async Task Refresh_StatiNuovi_ObstructedEIncompleteEConflittoAdAlbero()
    {
        var f = new Fixture().With(("a", SvnStatusKind.Obstructed), ("b", SvnStatusKind.Incomplete));
        f.Svn.Statuses.Add(new SvnFileStatus("c", SvnStatusKind.Conflicted, IsTreeConflict: true));
        await f.Refresh();

        Assert.Contains("Ostruito", f.Row("changes:a").Tooltip);
        Assert.Contains("Incompleto", f.Row("changes:b").Tooltip);
        Assert.Contains("Conflitto (albero)", f.Row("changes:c").Tooltip);
        Assert.Equal("!", f.Row("changes:a").Status);
    }

    [Fact]
    public async Task Refresh_VistaLista_MostraLaCartellaInDetail()
    {
        var f = new Fixture().With(("src/Core/A.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await f.Cmd("setView", new { view = "list" });

        Assert.Equal("list", f.Snap.View);
        Assert.Equal(["A.cs"], f.Names("staged"));
        var row = f.Section("staged").Rows.Single();
        Assert.Equal("src/Core", row.Detail);
        Assert.Equal(0, row.Depth);
    }

    [Fact]
    public async Task Refresh_ILetturaDelBranchSoloSeServe()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        await f.Refresh();
        Assert.Equal(1, f.Svn.BranchCalls);

        await f.Store.RefreshAsync(Ct, reloadBranch: true);
        Assert.Equal(2, f.Svn.BranchCalls);
    }

    [Fact]
    public async Task Refresh_ElementiFuoriDallaWorkingCopy_Ignorati()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), (@"..\fuori\x.cs", SvnStatusKind.Modified));
        await f.Refresh();

        Assert.Equal(["a.cs"], f.Names("staged"));
    }

    [Fact]
    public async Task Refresh_ErroreSuWorkingCopyAutoRilevata_NonRiapreLInputManuale()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Store.ApplyAutoDetectedWorkingCopyAsync(@"C:\rilevata", Ct);
        f.Svn.StatusError = "E155004 working copy locked";

        await f.Refresh();

        Assert.StartsWith("Errore", f.Status);
        Assert.True(f.Snap.Status.Err);
        Assert.False(f.Snap.WorkingCopy.ShowInput);
    }

    // ── Staged ───────────────────────────────────────────────────────

    [Fact]
    public async Task UnstageEStage_SpostanoUnFileTraLeSezioni()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Cmd("unstage", new { key = "staged:a.cs" });
        Assert.Equal(["changes:a.cs"], f.FileKeys("changes"));
        Assert.Empty(f.FileKeys("staged"));

        await f.Cmd("stage", new { key = "changes:a.cs" });
        await f.Cmd("stage", new { key = "unversioned:n.txt" });
        Assert.Equal(["staged:a.cs", "staged:n.txt"], f.FileKeys("staged"));
        Assert.Empty(f.FileKeys("unversioned"));
        Assert.Contains("verrà aggiunto con il commit", f.Row("staged:n.txt").StatusText);
    }

    [Fact]
    public async Task Stage_DiCartellaESezione_SpostaITuttiIFigli_MaNonIConflitti()
    {
        var f = new Fixture().With(("src/a.cs", SvnStatusKind.Modified), ("src/b.cs", SvnStatusKind.Modified), ("src/c.cs", SvnStatusKind.Conflicted),
            ("n/x.txt", SvnStatusKind.Unversioned), ("n/y.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "section:staged" });
        Assert.Equal(0, f.Section("staged").Count);

        await f.Cmd("stage", new { key = "changes:src" });
        Assert.Equal(["staged:src/a.cs", "staged:src/b.cs"], f.FileKeys("staged"));
        Assert.Equal(["changes:src/c.cs"], f.FileKeys("changes"));

        await f.Cmd("stage", new { key = "section:unversioned" });
        Assert.Equal(4, f.Section("staged").Count);
        Assert.Equal(0, f.Section("unversioned").Count);
    }

    [Fact]
    public async Task Staged_MantieneLaSceltaDellUtenteTraUnRefreshELAltro()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified));
        await f.RefreshOnly();
        await f.Cmd("stage", new { key = "changes:b.cs" });

        await f.RefreshOnly();

        Assert.Equal(["changes:a.cs"], f.FileKeys("changes"));
        Assert.Equal(["staged:b.cs"], f.FileKeys("staged"));
    }

    [Fact]
    public async Task Staged_ResistePerWorkingCopy_TornandoAllaPrimaLaSceltaRestaIntatta()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.RefreshOnly();
        await f.Cmd("stage", new { key = "changes:a.cs" });

        f.Store.WorkingCopyPath = @"C:\altra";
        f.Svn.Statuses.Clear();
        await f.RefreshOnly();
        Assert.Equal(0, f.Section("staged").Count);

        f.Svn.Statuses.Add(new SvnFileStatus("a.cs", SvnStatusKind.Modified));
        f.Store.WorkingCopyPath = Wc;
        await f.RefreshOnly();
        Assert.Equal(["staged:a.cs"], f.FileKeys("staged")); // la scelta per working copy resta
    }

    // ── Chiavi di riga e validazione ─────────────────────────────────

    [Theory]
    [InlineData("staged:../evil.txt")]
    [InlineData("changes:../../Windows/system.ini")]
    [InlineData("changes:inesistente.cs")]
    [InlineData("unversioned:a.cs")] // esiste ma e' in Staged, non in Non versionati
    [InlineData("a.cs")]
    [InlineData(":a.cs")]
    [InlineData("section:bogus")]
    [InlineData("bogus:a.cs")]
    [InlineData("changes:")]
    public async Task ChiaviNonValide_VengonoRifiutate_ENonSiTocca_Nulla(string key)
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("stage", new { key }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("rowOp", new { op = "revert", key }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("rowOp", new { op = "deleteFile", key }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("showDiff", new { key }));

        Assert.Empty(f.Svn.Reverted);
        Assert.Empty(f.Svn.Deleted);
        Assert.Empty(f.Recycled);
        Assert.Empty(f.Host.Diffs);
    }

    [Fact]
    public async Task ComandiEArgomentiNonValidi_Rifiutati()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("nonEsiste"));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("stage"));                       // manca key
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("stage", new { key = 5 }));      // tipo sbagliato
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("setView", new { view = "griglia" }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("setLanguage", new { lang = "klingon" }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("rowOp", new { op = "format", key = "staged:a.cs" }));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("prereqRun", new { id = "../x", primary = true }));
    }

    [Fact]
    public async Task ToggleFolder_SoloSuCartelleEsistenti()
    {
        var f = new Fixture().With(("src/a.cs", SvnStatusKind.Modified), ("src/b.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("toggleFolder", new { key = "staged:nope" }));
        await f.Cmd("toggleFolder", new { key = "staged:src" });

        Assert.False(f.Row("staged:src").Expanded);
        Assert.Equal(["src"], f.Names("staged"));
    }

    // ── Commit ───────────────────────────────────────────────────────

    [Fact]
    public async Task Commit_SoloFileInStaged()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "staged:b.cs" });
        await f.Cmd("setCommitText", new { text = "  fix  " });

        await f.Cmd("commit");

        var commit = Assert.Single(f.Svn.Commits);
        Assert.Equal([Full("a.cs")], commit.Paths);
        Assert.Equal("fix", commit.Message);
        Assert.Empty(f.Svn.Added);
        Assert.Contains("revisione 42", f.Status);
        Assert.Equal(string.Empty, f.Snap.Commit.Text);
    }

    [Fact]
    public async Task Commit_NonVersionatoInStaged_VieneAggiuntoPrima_EMancanteCancellato()
    {
        var f = new Fixture().With(("new.txt", SvnStatusKind.Unversioned), ("gone.txt", SvnStatusKind.Missing));
        await f.Refresh();
        await f.Cmd("stage", new { key = "unversioned:new.txt" });
        await f.Cmd("stage", new { key = "changes:gone.txt" }); // il Missing non e' staged di default
        await f.Cmd("setCommitText", new { text = "msg" });

        await f.Cmd("commit");

        Assert.Equal([Full("new.txt")], Assert.Single(f.Svn.Added));
        Assert.Equal([Full("gone.txt")], Assert.Single(f.Svn.Deleted));
        Assert.Equal(2, Assert.Single(f.Svn.Commits).Paths.Count);
    }

    [Fact]
    public async Task Commit_SoloUnFiglioDiCartellaAggiunta_IncludeLaCartellaPadre()
    {
        var f = new Fixture().With(
            (@"src\New", SvnStatusKind.Added),
            (@"src\New\a.cs", SvnStatusKind.Added),
            (@"src\New\b.cs", SvnStatusKind.Added));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "section:staged" });
        await f.Cmd("stage", new { key = "changes:src/New/a.cs" });
        await f.Cmd("setCommitText", new { text = "m" });

        await f.Cmd("commit");

        var commit = Assert.Single(f.Svn.Commits);
        Assert.Equal([Full(@"src\New"), Full(@"src\New\a.cs")], commit.Paths);
        Assert.Empty(f.Svn.Added); // la cartella e' gia' aggiunta: nessun add
        Assert.Empty(f.Svn.CommitWholeFolders);
    }

    [Fact]
    public async Task Commit_CartellaNonVersionataInStaged_VieneAggiuntaECommittataPerIntero()
    {
        var wc = Path.Combine(Path.GetTempPath(), "SVNAssist_VM_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(wc, "nuova"));
        try
        {
            using var f = new Fixture(wc).With(("nuova", SvnStatusKind.Unversioned));
            await f.Refresh();
            Assert.True(f.Row("unversioned:nuova").Dir);
            await f.Cmd("stage", new { key = "unversioned:nuova" });
            await f.Cmd("setCommitText", new { text = "m" });

            await f.Cmd("commit");

            var folder = Path.Combine(wc, "nuova");
            Assert.Equal([folder], Assert.Single(f.Svn.Added));
            Assert.Equal([folder], Assert.Single(f.Svn.Commits).Paths);
            Assert.Equal([folder], f.Svn.CommitWholeFolders);
        }
        finally
        {
            Directory.Delete(wc, recursive: true);
        }
    }

    [Fact]
    public async Task Commit_Fallito_MostraL_ErroreERileggeLaListaDopoAddEDelete()
    {
        var f = new Fixture().With(("new.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Cmd("stage", new { key = "unversioned:new.txt" });
        await f.Cmd("setCommitText", new { text = "msg" });
        f.Svn.CommitError = "file più recenti sul server";
        var statusCalls = f.Svn.StatusCalls;

        await f.Cmd("commit");

        Assert.StartsWith("Errore commit", f.Status);
        Assert.Contains("più recenti", f.Status);
        Assert.Equal(statusCalls + 1, f.Svn.StatusCalls);
        Assert.Equal("msg", f.Snap.Commit.Text); // il messaggio non si perde
    }

    [Fact]
    public async Task Commit_SenzaMessaggioONessunFile_NonCommitta()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await f.Cmd("commit");
        Assert.Empty(f.Svn.Commits);
        Assert.Contains("messaggio", f.Status);

    }

    [Fact]
    public async Task Commit_NienteDaCommittare_NonCommitta()
    {
        var f = new Fixture().With(("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "section:staged" });
        await f.Cmd("setCommitText", new { text = "m" });

        await f.Cmd("commit");

        Assert.Empty(f.Svn.Commits);
        Assert.Contains("Nessuna modifica da committare", f.Status);
        Assert.Equal(0, f.Snap.Commit.Count);
    }

    [Fact]
    public async Task Commit_StagedVuoto_CommittaTutteLeModificheVersionate_NonIlNonVersionato()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Added), ("d.cs", SvnStatusKind.Deleted),
            ("m.cs", SvnStatusKind.Missing), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "section:staged" });
        await f.Cmd("setCommitText", new { text = "tutto" });
        Assert.True(f.Snap.Commit.All);
        Assert.Equal(4, f.Snap.Commit.Count);

        await f.Cmd("commit");

        var commit = Assert.Single(f.Svn.Commits);
        Assert.Equivalent(new[] { Full("a.cs"), Full("b.cs"), Full("d.cs"), Full("m.cs") }, commit.Paths);
        Assert.DoesNotContain(Full("n.txt"), commit.Paths);
        Assert.Empty(f.Svn.Added);
        Assert.Contains(Full("m.cs"), f.Svn.Deleted.SelectMany(d => d)); // il mancante viene cancellato come delete
    }

    [Fact]
    public async Task Commit_StagedVuoto_ConflittoBlocca()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "section:staged" });
        await f.Cmd("setCommitText", new { text = "m" });

        await f.Cmd("commit");

        Assert.Empty(f.Svn.Commits);
        Assert.Contains("conflitti", f.Status);
    }

    [Fact]
    public async Task Commit_ConFileInStaged_ResteSoloQuelli_EilContatoreLoDice()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "staged:b.cs" });

        Assert.False(f.Snap.Commit.All);
        Assert.Equal(1, f.Snap.Commit.Count);
    }

    [Fact]
    public async Task Commit_FileInStagedDivenutoConflitto_Rifiuta()
    {
        var f = new Fixture().With(("c.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Svn.Statuses.Clear();
        f.Svn.Statuses.Add(new SvnFileStatus("c.cs", SvnStatusKind.Conflicted)); // dopo un update
        await f.Refresh();
        await f.Cmd("setCommitText", new { text = "m" });

        await f.Cmd("commit");

        Assert.Empty(f.Svn.Commits);
        Assert.Contains("conflitt", f.Status);
        Assert.Equal(["changes:c.cs"], f.FileKeys("changes"));
    }

    [Fact]
    public async Task DoppioClick_Commit_EseguitoUnaSolaVolta()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        await f.Cmd("setCommitText", new { text = "m" });
        f.Svn.CommitGate = new TaskCompletionSource();

        var first = f.Cmd("commit");
        await f.Cmd("commit"); // secondo click mentre il primo e' in corso: ignorato
        f.Svn.CommitGate.SetResult();
        await first;

        Assert.Single(f.Svn.Commits);
    }

    [Fact]
    public async Task RefreshAutomatico_DuranteUnaOperazione_VieneSaltato()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        await f.Cmd("setCommitText", new { text = "m" });
        f.Svn.CommitGate = new TaskCompletionSource();
        var calls = f.Svn.StatusCalls;

        var commit = f.Cmd("commit");
        await f.Store.AutoRefreshAsync(Ct); // watcher: non deve lanciare svn status mentre il commit tiene il lock
        Assert.Equal(calls, f.Svn.StatusCalls);

        f.Svn.CommitGate.SetResult();
        await commit;
        Assert.Equal(calls + 1, f.Svn.StatusCalls); // il refresh lo fa il commit alla fine
    }

    // ── Operazioni di riga ───────────────────────────────────────────

    [Fact]
    public async Task DeleteFile_Versionato_ConfermaESvnDeleteForce()
    {
        var f = new Fixture().With(("mod.cs", SvnStatusKind.Modified), ("gone.cs", SvnStatusKind.Missing));
        await f.Refresh();
        await f.Cmd("stage", new { key = "changes:gone.cs" }); // il Missing non e' staged di default

        await f.Cmd("rowOp", new { op = "deleteFile", key = "staged:mod.cs" });
        await f.Cmd("rowOp", new { op = "deleteFile", key = "staged:gone.cs" });

        Assert.Equal([[Full("mod.cs")], [Full("gone.cs")]], f.Svn.Deleted);
        Assert.Equal([true, true], f.Svn.DeleteForced);
        Assert.Contains("modifiche locali non committate", f.Dialogs.Messages[0]);
        Assert.DoesNotContain("modifiche locali", f.Dialogs.Messages[1]);
        Assert.Empty(f.Recycled);
    }

    [Fact]
    public async Task DeleteFile_NonVersionato_VaNelCestino_NonSvn()
    {
        var f = new Fixture().With(("tmp.txt", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Cmd("rowOp", new { op = "deleteFile", key = "unversioned:tmp.txt" });

        Assert.Equal([Full("tmp.txt")], f.Recycled);
        Assert.Empty(f.Svn.Deleted);
        Assert.Contains("cestino", Assert.Single(f.Dialogs.Messages));
    }

    [Fact]
    public async Task DeleteFile_SeAnnulli_NonCancellaNulla()
    {
        var f = new Fixture().With(("mod.cs", SvnStatusKind.Modified), ("tmp.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        f.Dialogs.ConfirmResult = false;

        await f.Cmd("rowOp", new { op = "deleteFile", key = "staged:mod.cs" });
        await f.Cmd("rowOp", new { op = "deleteFile", key = "unversioned:tmp.txt" });

        Assert.Empty(f.Svn.Deleted);
        Assert.Empty(f.Recycled);
    }

    [Fact]
    public async Task DeleteFile_SuCartellaOSezione_NonEseguito()
    {
        var f = new Fixture().With(("src/a.cs", SvnStatusKind.Modified), ("src/b.cs", SvnStatusKind.Modified));
        await f.Refresh();
        Assert.False(f.Row("staged:src").Can.DeleteFile);

        await f.Cmd("rowOp", new { op = "deleteFile", key = "staged:src" });
        await f.Cmd("rowOp", new { op = "deleteFile", key = "section:staged" });

        Assert.Empty(f.Svn.Deleted);
        Assert.Empty(f.Recycled);
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Revert_ChiedeConfermaENonFaNienteSeAnnulli()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Dialogs.ConfirmResult = false;

        await f.Cmd("rowOp", new { op = "revert", key = "staged:a.cs" });

        Assert.Empty(f.Svn.Reverted);
        Assert.Contains("non è annullabile", Assert.Single(f.Dialogs.Messages));
    }

    [Fact]
    public async Task Revert_Sezione_RipristinaSoloCioCheContiene_EUsaPercorsiEspliciti()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Deleted), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Cmd("rowOp", new { op = "revert", key = "section:staged" });

        Assert.Equal([Full("a.cs"), Full("b.cs")], Assert.Single(f.Svn.Reverted));
        Assert.Equal([false], f.Svn.RevertRecursive);
    }

    [Fact]
    public async Task Revert_CartellaAggiunta_RicorsivoEConfermaElencaAncheIFigli()
    {
        var f = new Fixture().With(("New", SvnStatusKind.Added), (@"New\x.cs", SvnStatusKind.Added));
        await f.Refresh();

        await f.Cmd("rowOp", new { op = "revert", key = "staged:New" });

        Assert.Contains("New/x.cs", Assert.Single(f.Dialogs.Messages));
        Assert.Equal([true], f.Svn.RevertRecursive);
    }

    [Fact]
    public async Task Add_SuSezione_AggiungeTuttiINonVersionati_ERestanoInStaged()
    {
        var f = new Fixture().With(("a.txt", SvnStatusKind.Unversioned), ("b.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        f.Svn.Statuses.Clear();
        f.Svn.Statuses.Add(new SvnFileStatus("a.txt", SvnStatusKind.Added));
        f.Svn.Statuses.Add(new SvnFileStatus("b.txt", SvnStatusKind.Added));

        await f.Cmd("rowOp", new { op = "add", key = "section:unversioned" });

        Assert.Equal([Full("a.txt"), Full("b.txt")], Assert.Single(f.Svn.Added));
        Assert.Equal(["staged:a.txt", "staged:b.txt"], f.FileKeys("staged"));
    }

    [Fact]
    public async Task Ignore_SuCartella_ChiedeConfermaEIgnoraOgniElemento()
    {
        var f = new Fixture().With(("obj2/a.tmp", SvnStatusKind.Unversioned), ("obj2/b.tmp", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Cmd("rowOp", new { op = "ignore", key = "unversioned:obj2" });

        Assert.Equal([Full(@"obj2\a.tmp"), Full(@"obj2\b.tmp")], f.Svn.Ignored);
        Assert.Single(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Resolve_UsaLaScelta_DellUtente_ESeAnnulliNonRisolve()
    {
        var f = new Fixture().With(("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();

        f.Dialogs.ConflictResult = null;
        await f.Cmd("rowOp", new { op = "resolve", key = "changes:c.cs" });
        Assert.Empty(f.Svn.Resolved);

        f.Dialogs.ConflictResult = SvnConflictChoice.MineFull;
        await f.Cmd("rowOp", new { op = "resolve", key = "changes:c.cs" });
        Assert.Equal((Full("c.cs"), SvnConflictChoice.MineFull), Assert.Single(f.Svn.Resolved));
    }

    [Fact]
    public async Task Resolve_ConflittoAdAlbero_ChiedeConfermaEUsaWorking()
    {
        var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("t.cs", SvnStatusKind.Conflicted, IsTreeConflict: true));
        await f.Refresh();

        await f.Cmd("rowOp", new { op = "resolve", key = "changes:t.cs" });

        Assert.Equal(SvnConflictChoice.Working, Assert.Single(f.Svn.Resolved).Choice);
        Assert.Contains("albero", Assert.Single(f.Dialogs.Messages));
    }

    [Fact]
    public async Task Can_DipendeDaStatoESezione()
    {
        var f = new Fixture().With(("m.cs", SvnStatusKind.Modified), ("n.txt", SvnStatusKind.Unversioned), ("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();

        var m = f.Row("staged:m.cs").Can;
        Assert.True(m.Diff && m.Unstage && m.Revert && m.DeleteFile);
        Assert.False(m.Stage || m.Add || m.Ignore || m.Resolve);

        var n = f.Row("unversioned:n.txt").Can;
        Assert.True(n.Stage && n.Add && n.Ignore && n.DeleteFile);
        Assert.False(n.Diff || n.Unstage || n.Revert);

        var c = f.Row("changes:c.cs").Can;
        Assert.True(c.Resolve && c.Revert && c.Diff);
        Assert.False(c.Stage);
    }

    // ── Albero: espandi / comprimi ───────────────────────────────────

    [Fact]
    public async Task Espandi_Comprimi_SezioneECartelle()
    {
        var f = new Fixture().With(("src/a.cs", SvnStatusKind.Modified), ("src/b/c.cs", SvnStatusKind.Modified));
        await f.Refresh();
        Assert.Contains("a.cs", f.Names("staged"));

        await f.Cmd("toggleFolder", new { key = "staged:src" });
        Assert.DoesNotContain("a.cs", f.Names("staged"));

        await f.Cmd("expandAll");
        Assert.Contains("a.cs", f.Names("staged"));

        await f.Cmd("collapseAll");
        Assert.Equal(["src"], f.Names("staged"));

        await f.Cmd("expandAll");
        await f.Cmd("toggleSection", new { sec = "staged" });
        Assert.False(f.Section("staged").Expanded);
        Assert.True(f.Section("changes").Expanded);
    }

    // ── Diff ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ShowDiff_EsportaBaseEApreIlDiffNellEditorDellHost()
    {
        var wc = Path.Combine(Path.GetTempPath(), "SVNAssist_VM_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(wc);
        File.WriteAllText(Path.Combine(wc, "a.cs"), "modificato");
        try
        {
            using var f = new Fixture(wc).With(("a.cs", SvnStatusKind.Modified));
            await f.Refresh();

            await f.Cmd("showDiff", new { key = "staged:a.cs" });

            var diff = Assert.Single(f.Host.Diffs);
            Assert.Equal(Path.Combine(wc, "a.cs"), diff.Right);
            Assert.StartsWith(f.TempRoot, diff.Left);
            Assert.Equal("a.cs", Path.GetFileName(diff.Left));
            Assert.Equal("BASE di a.cs", File.ReadAllText(diff.Left));
            Assert.Equal((Path.Combine(wc, "a.cs"), diff.Left), f.Svn.Exported.Single());
        }
        finally
        {
            Directory.Delete(wc, recursive: true);
        }
    }

    [Fact]
    public async Task ShowDiff_FileMancante_ConfrontaConUnFileVuoto()
    {
        var f = new Fixture().With(("gone.cs", SvnStatusKind.Missing));
        using var _ = f;
        await f.Refresh();
        await f.Cmd("stage", new { key = "changes:gone.cs" }); // il Missing non e' staged di default

        await f.Cmd("showDiff", new { key = "staged:gone.cs" });

        var diff = Assert.Single(f.Host.Diffs);
        Assert.Empty(File.ReadAllBytes(diff.Right));
        Assert.NotEqual(Full("gone.cs"), diff.Right);
    }

    [Fact]
    public async Task ShowDiff_NonVersionato_NonApreNulla()
    {
        var f = new Fixture().With(("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Cmd("showDiff", new { key = "unversioned:n.txt" });

        Assert.Empty(f.Host.Diffs);
        Assert.Contains("non è versionato", f.Status);
    }

    [Fact]
    public async Task ShowFolderDiff_Cartella_ApreLElencoFileNelLog()
    {
        using var f = new Fixture().With(("src/a.cs", SvnStatusKind.Modified), ("src/b.cs", SvnStatusKind.Added));
        await f.Refresh();

        await f.Cmd("showFolderDiff", new { key = "staged:src" });

        var path = Assert.Single(f.Host.FolderDiffs);
        Assert.EndsWith("src", path);
        Assert.Empty(f.Host.OpenedFiles);
    }

    // ── Working copy, solution, impostazioni ─────────────────────────

    [Fact]
    public async Task SettingWorkingCopyPath_VinceSullAutoDetect()
    {
        var f = new Fixture();
        f.Settings.WorkingCopy = @"C:\config";

        await f.Store.ApplyAutoDetectedWorkingCopyAsync(@"C:\detected", Ct);

        Assert.Equal(@"C:\config", f.Store.WorkingCopyPath);
    }

    [Fact]
    public async Task AutoDetect_StessaWorkingCopy_NonRifaIlRefresh()
    {
        var f = new Fixture();
        await f.Store.ApplyAutoDetectedWorkingCopyAsync(@"C:\other", Ct);
        var calls = f.Svn.StatusCalls;

        await f.Store.ApplyAutoDetectedWorkingCopyAsync(@"C:\other", Ct);

        Assert.Equal(calls, f.Svn.StatusCalls);
    }

    [Fact]
    public async Task AutoDetect_SolutionNonSvn_NonAzzeraUnPercorsoScrittoAMano()
    {
        var f = new Fixture();

        await f.Store.ApplyAutoDetectedWorkingCopyAsync(null, Ct);

        Assert.Equal(Wc, f.Store.WorkingCopyPath);
    }

    [Fact]
    public async Task SetWorkingCopy_PercorsoInesistenteONonSvn_Rifiutato()
    {
        var f = new Fixture();
        var plain = Path.Combine(Path.GetTempPath(), "SVNAssist_plain_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(plain);
        try
        {
            await f.Cmd("setWorkingCopy", new { path = Path.Combine(plain, "non-esiste") });
            Assert.StartsWith("Errore", f.Status);
            await f.Cmd("setWorkingCopy", new { path = plain });
            Assert.Contains("non è una working copy", f.Status);
            Assert.Equal(Wc, f.Store.WorkingCopyPath);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Fact]
    public async Task SetTheme_SalvaNellHost_EIlTemaAutoSegueQuelloDiVs()
    {
        var f = new Fixture();
        Assert.Equal(new ThemeDto("auto", "dark"), f.Snap.Theme);

        f.Store.SetVsThemeKind("blue");
        Assert.Equal(new ThemeDto("auto", "blue"), f.Snap.Theme);

        await f.Cmd("setTheme", new { mode = "light" });
        Assert.Equal(new ThemeDto("light", "light"), f.Snap.Theme);
        Assert.Equal("light", f.Settings.Theme);

        await Assert.ThrowsAsync<ArgumentException>(() => f.Cmd("setTheme", new { mode = "rosa" }));
    }

    // ── AI e lingua ──────────────────────────────────────────────────

    [Fact]
    public async Task Ai_GenerateUsaSoloIFileInStaged_ELaLinguaScelta()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "staged:b.cs" });

        await f.Cmd("setLanguage", new { lang = "english" });
        await f.Cmd("genAi");

        Assert.Equal(["english"], f.Ai.Service.Languages);
        Assert.Equal("messaggio generato", f.Snap.Commit.Text);
        Assert.Equal([Full("a.cs")], f.Svn.AiDiffRequests[0]);
        Assert.False(f.Snap.Commit.Busy);
    }

    [Fact]
    public async Task Ai_LinguaIniziale_ArrivaDalleImpostazioni_EValeAncheInMigliora()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        f.Settings.Language = "italiano";
        await f.Refresh();
        await f.Store.CommitMessage.InitializeAsync(Ct);
        Assert.Equal("italiano", f.Snap.Commit.Lang);

        await f.Cmd("setCommitText", new { text = "bozza" });
        await f.Cmd("improveAi");

        Assert.Equal(["italiano"], f.Ai.Service.Languages);
        Assert.Equal("migliorato: bozza", f.Snap.Commit.Text);
    }

    [Fact]
    public async Task Ai_ScelteDelPannelloNonVengonoSovrascrittedallaLetturaDelleImpostazioni()
    {
        var f = new Fixture();
        f.Settings.Language = "italiano";

        await f.Cmd("setLanguage", new { lang = "english" });
        await f.Store.CommitMessage.InitializeAsync(Ct);

        Assert.Equal("english", f.Snap.Commit.Lang);
    }

    [Fact]
    public async Task Ai_SenzaProvider_MostraLAiutoESpiegaNelloStato()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        f.Ai.Available = false;
        await f.Refresh();

        await f.Cmd("genAi");

        Assert.Contains("Nessun provider AI", f.Snap.Commit.AiStatus);
    }

    [Fact]
    public async Task Ai_SenzaFileInStaged_AvvisaSenzaChiamareLAi()
    {
        var f = new Fixture().With(("n.txt", SvnStatusKind.Unversioned)); // i non versionati non fanno parte del commit
        await f.Refresh();

        await f.Cmd("genAi");

        Assert.Contains("Nessuna modifica da committare", f.Snap.Commit.AiStatus);
        Assert.Empty(f.Ai.Service.Languages);
    }

    [Fact]
    public async Task Ai_StagedVuoto_UsaTutteLeModificheVersionate()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Cmd("unstage", new { key = "section:staged" });

        await f.Cmd("genAi");

        Assert.Single(f.Ai.Service.Languages); // genera dalle modifiche versionate
    }

    // ── Concorrenza ──────────────────────────────────────────────────

    [Fact]
    public async Task RefreshEComandiConcorrenti_NonCorrompononoLoStato()
    {
        var items = Enumerable.Range(0, 60).Select(i => ($"d{i % 6}/f{i}.cs", SvnStatusKind.Modified)).ToArray();
        var f = new Fixture().With(items);
        await f.Refresh();

        var errors = new List<Exception>();
        Task Loop(Func<Task> body) => Task.Run(async () =>
        {
            try { for (var i = 0; i < 100; i++) await body(); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });

        await Task.WhenAll(
            Loop(() => f.Store.RefreshAsync(Ct)),
            Loop(() => f.Cmd("collapseAll")),
            Loop(() => f.Cmd("expandAll")),
            Loop(() => f.Cmd("stage", new { key = "section:changes" })),
            Loop(() => f.Cmd("unstage", new { key = "section:staged" })),
            Loop(() => f.Cmd("setView", new { view = "list" })),
            Loop(() => f.Cmd("setView", new { view = "tree" })),
            Loop(() => { _ = f.Store.GetSnapshot(); return Task.CompletedTask; }));

        Assert.Empty(errors);
    }
}
