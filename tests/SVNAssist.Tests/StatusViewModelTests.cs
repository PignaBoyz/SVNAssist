using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.ToolWindows;

namespace SVNAssist.Tests;

/// <summary>ViewModel del pannello SVN Changes con servizi finti (nessun svn, nessuna VS).</summary>
public class StatusViewModelTests
{
    private const string Wc = @"C:\wc";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Fixture
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeAiFactory Ai { get; } = new();
        public StubSettings Settings { get; } = new();
        public SvnStatusToolWindowData Vm { get; }

        public Fixture()
        {
            Vm = new SvnStatusToolWindowData(
                Settings, Svn, Ai, new NoopAiPrompt(), Dialogs,
                new PrerequisitesViewModel(new PrerequisitesService(Settings, new PrerequisiteProbes())))
            {
                WorkingCopyPath = Wc,
            };
        }

        public Fixture With(params (string Path, SvnStatusKind Status)[] items)
        {
            foreach (var (path, status) in items)
                Svn.Statuses.Add(new SvnFileStatus(path, status, IsTreeConflict: false));
            return this;
        }

        public Task Refresh() => Vm.RefreshAsync(Ct);

        public ChangeRowViewModel Row(string name) => Vm.Rows.Single(r => r.Name == name);

        public IEnumerable<string> RowNames => Vm.Rows.Select(r => r.Name);
    }

    private static string Full(string relative) => Path.Combine(Wc, relative);

    [Fact]
    public async Task Refresh_CostruisceSezioniAlberoECompattaLeCartelle()
    {
        var f = new Fixture().With(
            (@"src\Core\Services\A.cs", SvnStatusKind.Modified),
            (@"src\Core\Services\B.cs", SvnStatusKind.Modified),
            (@"src\Program.cs", SvnStatusKind.Added),
            ("notes.txt", SvnStatusKind.Unversioned),
            (@"src\Conf.cs", SvnStatusKind.Conflicted));

        await f.Refresh();

        Assert.Equal(
            ["Conflitti (1)", "src", "Conf.cs",
             "Modifiche (3)", "src", @"Core\Services", "A.cs", "B.cs", "Program.cs",
             "Non versionati (1)", "notes.txt"],
            f.RowNames);
        Assert.Equal("trunk", f.Vm.BranchName);
        Assert.False(f.Vm.IsEmpty);
    }

    [Fact]
    public async Task SelezionaTutto_SelezionaSoloLaSezioneModifiche()
    {
        var f = new Fixture().With(
            ("a.cs", SvnStatusKind.Modified),
            (@"bin\x.dll", SvnStatusKind.Unversioned));
        await f.Refresh();
        await f.Vm.SetAllSelectedAsync(false);

        await f.Vm.SetAllSelectedAsync(true);

        Assert.True(f.Row("a.cs").CheckedState);
        Assert.False(f.Row("x.dll").CheckedState);
    }

    [Fact]
    public async Task SettingWorkingCopyPath_VinceSullAutoDetect()
    {
        var f = new Fixture();
        f.Settings.WorkingCopy = @"C:\config";

        await f.Vm.ApplyAutoDetectedWorkingCopyAsync(@"C:\detected", Ct);

        Assert.Equal(@"C:\config", f.Vm.WorkingCopyPath);
    }

    [Fact]
    public async Task Refresh_VistaLista_MostraLaCartellaInGrigio()
    {
        var f = new Fixture().With((@"src\Core\A.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await f.Vm.SetTreeViewAsync(false);

        Assert.Equal(["Modifiche (1)", "A.cs"], f.RowNames);
        Assert.Equal(@"src\Core", f.Row("A.cs").Detail);
        Assert.False(f.Vm.IsTreeView);
    }

    [Fact]
    public async Task Refresh_StatiNuovi_ObstructedEIncompleteEConflittoAdAlbero()
    {
        var f = new Fixture().With(("a", SvnStatusKind.Obstructed), ("b", SvnStatusKind.Incomplete));
        f.Svn.Statuses.Add(new SvnFileStatus("c", SvnStatusKind.Conflicted, IsTreeConflict: true));
        await f.Refresh();

        Assert.Contains("Ostruito", f.Row("a").ToolTip);
        Assert.Contains("Incompleto", f.Row("b").ToolTip);
        Assert.Contains("Conflitto (albero)", f.Row("c").ToolTip);
    }

    [Fact]
    public async Task Refresh_SelezioneDiDefault_ModificatiSiNonVersionatiENo()
    {
        var f = new Fixture().With(("m.cs", SvnStatusKind.Modified), ("u.cs", SvnStatusKind.Unversioned), ("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();

        Assert.True(f.Row("m.cs").CheckedState);
        Assert.False(f.Row("u.cs").CheckedState);
        Assert.False(f.Row("c.cs").CheckedState);
    }

    [Fact]
    public async Task Refresh_MantieneLaSceltaDellUtenteTraUnRefreshELAltro()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Row("a.cs").IsChecked = false;

        await f.Refresh();

        Assert.False(f.Row("a.cs").CheckedState);
        Assert.True(f.Row("b.cs").CheckedState);
    }

    [Fact]
    public async Task Refresh_ILetturaDelBranchSoloSeServe()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));

        await f.Refresh();
        await f.Refresh();
        await f.Refresh();
        Assert.Equal(1, f.Svn.BranchCalls);

        await f.Vm.UpdateAsync(null, Ct);
        Assert.Equal(2, f.Svn.BranchCalls);
    }

    [Fact]
    public async Task Refresh_StrutturaInvariata_RiusaLeRigheEsistenti()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        var before = f.Vm.Rows.ToList();

        await f.Refresh();

        Assert.True(before.SequenceEqual(f.Vm.Rows));
    }

    // ── Commit ───────────────────────────────────────────────────────

    [Fact]
    public async Task Commit_SoloFileSelezionati()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        f.Row("b.cs").IsChecked = false;
        f.Vm.CommitMessage.Text = "  fix  ";

        await f.Vm.CommitAsync(null, Ct);

        var commit = Assert.Single(f.Svn.Commits);
        Assert.Equal([Full("a.cs")], commit.Paths);
        Assert.Equal("fix", commit.Message);
        Assert.Empty(f.Svn.Added);
        Assert.Contains("revisione 42", f.Vm.StatusMessage);
        Assert.Equal(string.Empty, f.Vm.CommitMessage.Text);
    }

    [Fact]
    public async Task Commit_NonVersionatoSpuntato_VieneAggiuntoPrima_EMancanteCancellato()
    {
        var f = new Fixture().With(("new.txt", SvnStatusKind.Unversioned), ("gone.txt", SvnStatusKind.Missing));
        await f.Refresh();
        f.Row("new.txt").IsChecked = true;
        f.Row("gone.txt").IsChecked = true; // i mancanti non sono spuntati di default
        f.Vm.CommitMessage.Text = "msg";

        await f.Vm.CommitAsync(null, Ct);

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
        await f.Vm.SetAllSelectedAsync(false);
        f.Row("a.cs").IsChecked = true;
        f.Vm.CommitMessage.Text = "m";

        await f.Vm.CommitAsync(null, Ct);

        var commit = Assert.Single(f.Svn.Commits);
        Assert.Equal([Full(@"src\New"), Full(@"src\New\a.cs")], commit.Paths);
        Assert.Empty(f.Svn.Added); // la cartella è già aggiunta: nessun add
        Assert.Empty(f.Svn.CommitWholeFolders);
    }

    [Fact]
    public async Task Commit_PadreNonSpuntatoMaVersionato_NonVieneAggiunto()
    {
        var f = new Fixture().With((@"src\Old\a.cs", SvnStatusKind.Modified), (@"src", SvnStatusKind.Modified));
        await f.Refresh();
        await f.Vm.SetAllSelectedAsync(false);
        f.Row("a.cs").IsChecked = true;
        f.Vm.CommitMessage.Text = "m";

        await f.Vm.CommitAsync(null, Ct);

        // Una cartella solo "modificata" (proprietà) non è un padre nuovo: non si committa se non spuntata.
        Assert.Equal([Full(@"src\Old\a.cs")], Assert.Single(f.Svn.Commits).Paths);
    }

    [Fact]
    public async Task Commit_CartellaNonVersionataSpuntata_VieneAggiuntaECommittataPerIntero()
    {
        var wc = Path.Combine(Path.GetTempPath(), "SVNAssist_VM_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(wc, "nuova"));
        try
        {
            var f = new Fixture().With(("nuova", SvnStatusKind.Unversioned));
            f.Vm.WorkingCopyPath = wc;
            await f.Refresh();
            f.Row("nuova\\").IsChecked = true;
            f.Vm.CommitMessage.Text = "m";

            await f.Vm.CommitAsync(null, Ct);

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
    public async Task Delete_ForceSoloPerFileConModificheLocali()
    {
        var f = new Fixture().With(
            ("mod.cs", SvnStatusKind.Modified), ("add.cs", SvnStatusKind.Added), ("gone.cs", SvnStatusKind.Missing),
            ("ok.cs", SvnStatusKind.Deleted));
        await f.Refresh();

        await f.Vm.DeleteAsync(f.Row("mod.cs"), Ct);
        await f.Vm.DeleteAsync(f.Row("add.cs"), Ct);
        await f.Vm.DeleteAsync(f.Row("gone.cs"), Ct);

        Assert.Equal([true, true, false], f.Svn.DeleteForced);
    }

    [Fact]
    public async Task Delete_SeAnnulli_NonPassaForceNeCancella()
    {
        var f = new Fixture().With(("mod.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Dialogs.ConfirmResult = false;

        await f.Vm.DeleteAsync(f.Row("mod.cs"), Ct);

        Assert.Empty(f.Svn.DeleteForced);
    }

    [Fact]
    public async Task Commit_Fallito_MostraL_ErroreERileggeLaListaDopoAddEDelete()
    {
        var f = new Fixture().With(("new.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        f.Row("new.txt").IsChecked = true;
        f.Vm.CommitMessage.Text = "msg";
        f.Svn.CommitError = "file più recenti sul server";
        var statusCalls = f.Svn.StatusCalls;

        await f.Vm.CommitAsync(null, Ct);

        Assert.Contains("Errore commit: file più recenti", f.Vm.StatusMessage);
        Assert.Equal(statusCalls + 1, f.Svn.StatusCalls);
        Assert.Equal("msg", f.Vm.CommitMessage.Text); // il messaggio non va perso
    }

    // ── Revert / Delete su cartelle, concorrenza, doppio click ───────

    [Fact]
    public async Task Revert_CartellaConSoloProprietaModificate_NonTocca_IFigli()
    {
        var f = new Fixture().With((".", SvnStatusKind.Modified), ("a.cs", SvnStatusKind.Modified));
        f.Vm.IsDirectory = p => p == Wc;
        await f.Refresh();

        await f.Vm.RevertAsync(f.Row("(cartella radice)"), Ct);

        Assert.Equal([Wc], Assert.Single(f.Svn.Reverted));
        Assert.Equal([false], f.Svn.RevertRecursive);
        Assert.Contains("1 elementi", f.Dialogs.Messages[0]);
    }

    [Fact]
    public async Task Revert_CartellaAggiunta_RicorsivoEConfermaElencaAncheIFigli()
    {
        var f = new Fixture().With((@"n", SvnStatusKind.Added), (@"n\x.cs", SvnStatusKind.Added), (@"n\y.cs", SvnStatusKind.Added));
        f.Vm.IsDirectory = p => p == Full("n");
        await f.Refresh();

        await f.Vm.RevertAsync(f.Row("n\\"), Ct);

        Assert.Equal([Full("n")], f.Svn.Reverted[0]);
        Assert.True(f.Svn.RevertRecursive[0]);
        Assert.Single(f.Svn.Reverted); // i figli sono coperti dalla chiamata ricorsiva
        Assert.Contains("3 elementi", f.Dialogs.Messages[0]);
        Assert.Contains(@"n\x.cs", f.Dialogs.Messages[0]);
    }

    [Fact]
    public async Task Delete_SuCartella_NonOfferto_ENonEseguito()
    {
        var f = new Fixture().With(("src", SvnStatusKind.Modified));
        f.Vm.IsDirectory = p => p == Full("src");
        await f.Refresh();

        Assert.False(f.Row("src\\").CanDelete);
        await f.Vm.DeleteAsync(f.Row("src\\"), Ct);

        Assert.Empty(f.Svn.Deleted);
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Refresh_ElementiMancanti_NonSonoSpuntatiDiDefault()
    {
        var f = new Fixture().With(("gone.cs", SvnStatusKind.Missing), ("m.cs", SvnStatusKind.Modified));
        await f.Refresh();

        Assert.False(f.Row("gone.cs").CheckedState);
        Assert.True(f.Row("m.cs").CheckedState);
    }

    [Fact]
    public async Task DoppioClick_Commit_EseguitoUnaSolaVolta()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Vm.CommitMessage.Text = "m";
        f.Svn.CommitGate = new TaskCompletionSource();

        var first = f.Vm.CommitAsync(null, Ct);
        await f.Vm.CommitAsync(null, Ct); // secondo click mentre il primo è in corso: ignorato
        f.Svn.CommitGate.SetResult();
        await first;

        Assert.Single(f.Svn.Commits);
    }

    [Fact]
    public async Task RefreshAutomatico_DuranteUnaOperazione_VieneSaltato()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Vm.CommitMessage.Text = "m";
        f.Svn.CommitGate = new TaskCompletionSource();
        var calls = f.Svn.StatusCalls;

        var commit = f.Vm.CommitAsync(null, Ct);
        await f.Refresh(); // watcher: non deve lanciare svn status mentre il commit tiene il lock
        Assert.Equal(calls, f.Svn.StatusCalls);

        f.Svn.CommitGate.SetResult();
        await commit;
        Assert.Equal(calls + 1, f.Svn.StatusCalls); // il refresh lo fa il commit alla fine
    }

    [Fact]
    public async Task Refresh_ErroreSuWorkingCopyAutoRilevata_NonRiapreLInputManuale()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Vm.ApplyAutoDetectedWorkingCopyAsync(@"C:\rilevata", Ct);
        f.Svn.StatusError = "E155004 working copy locked";

        await f.Refresh();

        Assert.Contains("Errore", f.Vm.StatusMessage);
        Assert.False(f.Vm.ShowWorkingCopyInput);
    }

    [Fact]
    public async Task RefreshEToggleConcorrenti_NonCorrompononoLoStato()
    {
        var items = Enumerable.Range(0, 60).Select(i => ($@"d{i % 6}\f{i}.cs", SvnStatusKind.Modified)).ToArray();
        var f = new Fixture().With(items);
        await f.Refresh();

        var errors = new List<Exception>();
        Task Loop(Func<Task> body) => Task.Run(async () =>
        {
            try { for (var i = 0; i < 150; i++) await body(); }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });

        await Task.WhenAll(
            Loop(() => f.Vm.RefreshAsync(Ct)),
            Loop(() => f.Vm.SetAllExpandedAsync(false)),
            Loop(() => f.Vm.SetAllExpandedAsync(true)),
            Loop(() => f.Vm.SetAllSelectedAsync(true)),
            Loop(() => f.Vm.SetTreeViewAsync(true)),
            Loop(() => f.Vm.SetTreeViewAsync(false)),
            Loop(() =>
            {
                var row = f.Vm.Rows.ToList().FirstOrDefault();
                if (row is not null)
                    row.IsChecked = false;
                return Task.CompletedTask;
            }));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Commit_SenzaMessaggioONessunFile_NonCommitta()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await f.Vm.CommitAsync(null, Ct);
        Assert.Empty(f.Svn.Commits);
        Assert.Contains("messaggio", f.Vm.StatusMessage);

        f.Vm.CommitMessage.Text = "m";
        await f.Vm.SetAllSelectedAsync(false);
        await f.Vm.CommitAsync(null, Ct);
        Assert.Empty(f.Svn.Commits);
    }

    [Fact]
    public async Task Commit_ConConflittoSelezionato_Rifiuta()
    {
        var f = new Fixture().With(("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();
        f.Row("c.cs").IsChecked = true;
        f.Vm.CommitMessage.Text = "m";

        await f.Vm.CommitAsync(null, Ct);

        Assert.Empty(f.Svn.Commits);
        Assert.Contains("conflitt", f.Vm.StatusMessage);
    }

    // ── Revert, Delete, Add, Ignore, Resolve ─────────────────────────

    [Fact]
    public async Task Revert_ChiedeConfermaENonFaNienteSeAnnulli()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Dialogs.ConfirmResult = false;

        await f.Vm.RevertAsync(f.Row("a.cs"), Ct);

        Assert.Empty(f.Svn.Reverted);
        Assert.Contains("a.cs", Assert.Single(f.Dialogs.Messages));
    }

    [Fact]
    public async Task Revert_ConConferma_AnnullaIFileElencati()
    {
        var f = new Fixture().With((@"src\a.cs", SvnStatusKind.Modified), (@"src\b.cs", SvnStatusKind.Added), ("other.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await f.Vm.RevertAsync(f.Row("src"), Ct);

        Assert.Equal([Full(@"src\a.cs"), Full(@"src\b.cs")], Assert.Single(f.Svn.Reverted));
        Assert.Contains("2 elementi", Assert.Single(f.Dialogs.Messages));
    }

    [Fact]
    public async Task Revert_Sezione_RipristinaTuttoCioCheeInModifiche()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Deleted), ("n.txt", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Vm.RevertAsync(f.Row("Modifiche (2)"), Ct);

        Assert.Equal(2, Assert.Single(f.Svn.Reverted).Count);
    }

    [Fact]
    public async Task Delete_FileModificato_AvvisaCheLeModificheLocaliSiPerdono()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Missing));
        await f.Refresh();

        await f.Vm.DeleteAsync(f.Row("a.cs"), Ct);
        Assert.Contains("andranno perse", f.Dialogs.Messages[0]);
        Assert.Single(f.Svn.Deleted);

        f.Dialogs.Messages.Clear();
        await f.Vm.DeleteAsync(f.Row("b.cs"), Ct);
        Assert.DoesNotContain("andranno perse", f.Dialogs.Messages[0]);
    }

    [Fact]
    public async Task Delete_SeAnnulli_NonCancella()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();
        f.Dialogs.ConfirmResult = false;

        await f.Vm.DeleteAsync(f.Row("a.cs"), Ct);

        Assert.Empty(f.Svn.Deleted);
    }

    [Fact]
    public async Task Add_SuCartellaNonVersionata_PassaIlPercorsoDellaCartella()
    {
        var f = new Fixture().With((@"src\New\a.txt", SvnStatusKind.Unversioned), (@"src\New\b.txt", SvnStatusKind.Unversioned));
        await f.Refresh();
        var folder = f.Vm.Rows.Single(r => r.IsFolder);
        Assert.Equal(@"src\New", folder.Name);

        await f.Vm.AddAsync(folder, Ct);

        Assert.Equal([Full(@"src\New")], Assert.Single(f.Svn.Added));
        // Gli elementi aggiunti restano spuntati per il commit.
        Assert.Equal(2, f.Vm.Rows.Count(r => r.IsFile && r.CheckedState == true));
    }

    [Fact]
    public async Task Add_SuSezione_AggiungeTuttiINonVersionati()
    {
        var f = new Fixture().With(("a.txt", SvnStatusKind.Unversioned), ("b.txt", SvnStatusKind.Unversioned), ("m.cs", SvnStatusKind.Modified));
        await f.Refresh();

        await f.Vm.AddAsync(f.Row("Non versionati (2)"), Ct);

        Assert.Equal([Full("a.txt"), Full("b.txt")], Assert.Single(f.Svn.Added));
    }

    [Fact]
    public async Task Ignore_SuCartella_ChiedeConfermaEIgnoraOgniElemento()
    {
        var f = new Fixture().With((@"obj2\a.tmp", SvnStatusKind.Unversioned), (@"obj2\b.tmp", SvnStatusKind.Unversioned));
        await f.Refresh();

        await f.Vm.IgnoreAsync(f.Vm.Rows.Single(r => r.IsFolder), Ct);

        Assert.Equal([Full(@"obj2\a.tmp"), Full(@"obj2\b.tmp")], f.Svn.Ignored);
        Assert.Single(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Resolve_UsaLaScelta_DellUtente()
    {
        var f = new Fixture().With(("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();
        f.Dialogs.ConflictResult = SvnConflictChoice.MineFull;

        await f.Vm.ResolveAsync(f.Row("c.cs"), Ct);

        Assert.Equal((Full("c.cs"), SvnConflictChoice.MineFull), Assert.Single(f.Svn.Resolved));
    }

    [Fact]
    public async Task Resolve_SeAnnulli_NonRisolve()
    {
        var f = new Fixture().With(("c.cs", SvnStatusKind.Conflicted));
        await f.Refresh();
        f.Dialogs.ConflictResult = null;

        await f.Vm.ResolveAsync(f.Row("c.cs"), Ct);

        Assert.Empty(f.Svn.Resolved);
    }

    [Fact]
    public async Task Resolve_ConflittoAdAlbero_ChiedeConfermaEUsaWorking()
    {
        var f = new Fixture();
        f.Svn.Statuses.Add(new SvnFileStatus("t.cs", SvnStatusKind.Conflicted, IsTreeConflict: true));
        await f.Refresh();

        await f.Vm.ResolveAsync(f.Row("t.cs"), Ct);

        Assert.Equal(SvnConflictChoice.Working, Assert.Single(f.Svn.Resolved).Choice);
        Assert.Contains("albero", Assert.Single(f.Dialogs.Messages));
    }

    // ── Checkbox tri-state, espansione, diff ─────────────────────────

    [Fact]
    public async Task Checkbox_Cartella_SelezionaEDeselezionaIDiscendenti_ETriStato()
    {
        var f = new Fixture().With((@"src\a.cs", SvnStatusKind.Modified), (@"src\b.cs", SvnStatusKind.Modified), ("z.cs", SvnStatusKind.Modified));
        await f.Refresh();
        var folder = f.Row("src");
        Assert.True(folder.CheckedState);

        f.Row("a.cs").IsChecked = false;
        Assert.Null(folder.CheckedState);
        Assert.Null(f.Row("Modifiche (3)").CheckedState);

        // Da "parziale" un clic seleziona tutto, poi deseleziona tutto.
        folder.IsChecked = null;
        Assert.True(folder.CheckedState);
        Assert.True(f.Row("a.cs").CheckedState);

        folder.IsChecked = false;
        Assert.False(folder.CheckedState);
        Assert.False(f.Row("b.cs").CheckedState);
        Assert.True(f.Row("z.cs").CheckedState);
    }

    [Fact]
    public async Task Checkbox_Sezione_ToccaTuttiIFile()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified), ("b.cs", SvnStatusKind.Modified));
        await f.Refresh();

        f.Row("Modifiche (2)").IsChecked = false;

        Assert.All(f.Vm.Rows.Where(r => r.IsFile), r => Assert.False(r.CheckedState));
    }

    [Fact]
    public async Task Espandi_Comprimi_SezioneECartelle()
    {
        var f = new Fixture().With((@"src\a.cs", SvnStatusKind.Modified), (@"src\b\c.cs", SvnStatusKind.Modified));
        await f.Refresh();
        Assert.Contains("a.cs", f.RowNames);

        await f.Vm.ToggleExpandAsync(f.Row("src"), Ct);
        Assert.DoesNotContain("a.cs", f.RowNames);
        Assert.Equal(ChangeRowFactory.CollapsedChevron, f.Row("src").Chevron);

        await f.Vm.SetAllExpandedAsync(true);
        Assert.Contains("a.cs", f.RowNames);

        await f.Vm.SetAllExpandedAsync(false);
        Assert.DoesNotContain("a.cs", f.RowNames);

        await f.Vm.ToggleExpandAsync(f.Row("Modifiche (2)"), Ct);
        Assert.Equal(["Modifiche (2)"], f.RowNames);
    }

    [Fact]
    public async Task DiffCartella_UneIDiffDeiSoliFileModificati()
    {
        var f = new Fixture().With((@"src\a.cs", SvnStatusKind.Modified), (@"src\b.cs", SvnStatusKind.Added));
        await f.Refresh();

        await f.Vm.ShowFolderDiffAsync(f.Row("src"), Ct);

        Assert.True(f.Vm.IsDiffVisible);
        Assert.Contains("diff of a.cs", f.Vm.DiffContent);
        Assert.Contains("diff of b.cs", f.Vm.DiffContent);
        Assert.Equal(2, f.Svn.FileDiffs.Count);
    }

    [Fact]
    public async Task AutoDetect_StessaWorkingCopy_NonRifaIlRefresh()
    {
        var f = new Fixture();
        await f.Vm.ApplyAutoDetectedWorkingCopyAsync(@"C:\other", Ct);
        var calls = f.Svn.StatusCalls;

        await f.Vm.ApplyAutoDetectedWorkingCopyAsync(@"C:\other", Ct);

        Assert.Equal(calls, f.Svn.StatusCalls);
    }

    [Fact]
    public async Task AutoDetect_SolutionNonSvn_NonAzzeraUnPercorsoScrittoAMano()
    {
        var f = new Fixture();

        await f.Vm.ApplyAutoDetectedWorkingCopyAsync(null, Ct);

        Assert.Equal(Wc, f.Vm.WorkingCopyPath);
    }

    // ── AI e lingua ──────────────────────────────────────────────────

    [Fact]
    public async Task Ai_LinguaScelta_EInglese_VieneUsataPerGenerare()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        await f.Refresh();

        f.Vm.CommitMessage.SelectedLanguage = "English";
        await f.Vm.CommitMessage.GenerateAsync(null, Ct);

        Assert.Equal(["english"], f.Ai.Service.Languages);
        Assert.Equal("messaggio generato", f.Vm.CommitMessage.Text);
        Assert.Equal([Full("a.cs")], f.Svn.AiDiffRequests[0]);
    }

    [Fact]
    public async Task Ai_LinguaIniziale_ArrivaDalleImpostazioni_EValeAncheInMigliora()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        f.Settings.Language = "italiano";
        await f.Refresh();
        await f.Vm.CommitMessage.InitializeAsync(Ct);
        Assert.Equal("Italiano", f.Vm.CommitMessage.SelectedLanguage);

        f.Vm.CommitMessage.Text = "bozza";
        await f.Vm.CommitMessage.ImproveAsync(null, Ct);

        Assert.Equal(["italiano"], f.Ai.Service.Languages);
    }

    [Fact]
    public async Task Ai_ScelteDelPannelloNonVengonoSovrascrittedallaLetturaDelleImpostazioni()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        f.Settings.Language = "italiano";
        await f.Refresh();

        f.Vm.CommitMessage.SelectedLanguage = "English";
        await f.Vm.CommitMessage.InitializeAsync(Ct);

        Assert.Equal("English", f.Vm.CommitMessage.SelectedLanguage);
    }

    [Fact]
    public async Task Ai_SenzaProvider_MostraLAiutoESpiegaNelloStato()
    {
        var f = new Fixture().With(("a.cs", SvnStatusKind.Modified));
        f.Ai.Available = false;
        await f.Refresh();

        await f.Vm.CommitMessage.GenerateAsync(null, Ct);

        Assert.Contains("Nessun provider AI", f.Vm.CommitMessage.AiStatus);
    }

    // ── Log ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Log_CaricaAltre50_AumentaIlLimite()
    {
        var svn = new FakeSvnService();
        var vm = new SvnLogToolWindowData(svn) { WorkingCopyPath = Wc };

        await vm.OnLoadLogAsync(null, Ct);
        Assert.Equal(50, svn.LogRequestedLimit);
        Assert.Equal(50, vm.LogEntries.Count);
        Assert.True(vm.CanLoadMore);

        await vm.OnLoadMoreAsync(null, Ct);
        Assert.Equal(100, svn.LogRequestedLimit);
        Assert.Equal(100, vm.LogEntries.Count);
    }

    [Fact]
    public async Task Log_QuandoFinisconoLeRevisioni_NonOffreAltroCaricamento()
    {
        var svn = new FakeSvnService { LogEntryCount = 10 };
        var vm = new SvnLogToolWindowData(svn) { WorkingCopyPath = Wc };

        await vm.OnLoadLogAsync(null, Ct);

        Assert.False(vm.CanLoadMore);
    }

    [Fact]
    public async Task Log_AutoDetect_ImpostaIlPercorsoECaricaIlLog()
    {
        var svn = new FakeSvnService();
        var vm = new SvnLogToolWindowData(svn);

        await vm.ApplyAutoDetectedWorkingCopyAsync(Wc, Ct);

        Assert.Equal(Wc, vm.WorkingCopyPath);
        Assert.NotEmpty(vm.LogEntries);
    }
}
