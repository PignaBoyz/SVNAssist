using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Registra (e a richiesta altera) le chiamate a <see cref="ISvnService"/>: per contare i giri a svn e simulare un processo terminato a meta'.</summary>
internal class SvnSpy : DispatchProxy
{
    internal ISvnService Inner { get; set; } = null!;
    internal List<string> Calls { get; } = [];

    /// <summary>Se impostato decide cosa fare di una chiamata: riceve il metodo, gli argomenti e la chiamata vera (da eseguire una volta).</summary>
    internal Func<MethodInfo, object?[], Func<object?>, object?>? Around { get; set; }

    internal static (ISvnService Service, SvnSpy Spy) Wrap(ISvnService inner)
    {
        var proxy = Create<ISvnService, SvnSpy>();
        var spy = (SvnSpy)(object)proxy;
        spy.Inner = inner;
        return (proxy, spy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        args ??= [];
        var m = targetMethod!;
        lock (Calls)
            Calls.Add(Describe(m, args));
        object? Run()
        {
            try { return m.Invoke(Inner, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        return Around is null ? Run() : Around(m, args, Run);
    }

    private static string Describe(MethodInfo m, object?[] args)
    {
        if (m.Name == "MergeRangesAsync")
        {
            var ranges = (IReadOnlyList<(long First, long Last)>)args[2]!;
            return $"merge{((bool)args[3]! ? "-dry" : "")} " + string.Join(",", ranges.Select(r => r.First == r.Last ? $"{r.First}" : $"{r.First}-{r.Last}"));
        }

        return m.Name.Replace("Async", string.Empty);
    }

    internal List<string> Merges(bool real) => Calls.Where(c => c.StartsWith(real ? "merge " : "merge-dry ", StringComparison.Ordinal)).ToList();
}

/// <summary>
/// "Continua merge" con svn vero (repository file://): conflitto alla 3a di 7 revisioni, stop dopo quella, stato su disco, riapertura (nuovi store sullo stesso file),
/// risoluzione, continuazione delle sole revisioni rimaste, risultato uguale a un merge unico; interruzione a meta' e "Annulla merge" che ripristina.
/// </summary>
[Collection("PatchBackup")]
public class MergeResumeRealTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Trunk (wc) con f1, f2, f3, sub/s.txt; branch <c>feat</c> con <paramref name="count"/> revisioni (r_n modifica f2 e f3; la <paramref name="conflictAt"/>-esima anche la riga 12 di f1);
    /// dopo ogni revisione un commit su un altro ramo (<c>other</c>), cosi' i numeri di revisione sono a salti. Trunk modifica la stessa riga di f1 della revisione in conflitto.
    /// </summary>
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SVNAssist_MgRes_" + Guid.NewGuid().ToString("N")[..8]);
        public string SvnExe { get; } = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn non trovato");
        public string Url { get; private set; } = "";
        public string Wc { get; private set; } = "";
        public List<long> FeatRevs { get; } = [];
        public FakeDialogs Dialogs { get; } = new();
        public FakeHost Host { get; } = new();
        public StubSettings Settings { get; } = new();
        public ISvnService Svn { get; private set; } = null!;
        public SvnSpy Spy { get; private set; } = null!;
        public ChangesStore Changes { get; private set; } = null!;
        public LogStore Log { get; private set; } = null!;
        public string RecentFile => Path.Combine(Root, "recent.json");

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

        public string P(string rel) => Path.Combine(Wc, rel.Replace('/', Path.DirectorySeparatorChar));

        private static string Lines(int n, Func<int, string> f) => string.Join("\n", Enumerable.Range(1, n).Select(f)) + "\n";

        public Env(int count = 7, int conflictAt = 3)
        {
            Directory.CreateDirectory(Root);
            var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin non trovato");
            var repo = Path.Combine(Root, "repo");
            Url = "file:///" + repo.Replace('\\', '/').TrimStart('/');
            if (!Url.StartsWith("file:///", StringComparison.Ordinal))
                Url = "file:///" + Url;
            Run($"create \"{repo}\"", Root, admin);
            Run($"mkdir -m init \"{Url}/trunk\" \"{Url}/branches\"", Root);
            Wc = Path.Combine(Root, "wc");
            Run($"checkout \"{Url}/trunk\" \"{Wc}\"", Root);
            File.WriteAllText(P("f1.txt"), Lines(30, i => "L" + i));
            File.WriteAllText(P("f2.txt"), "start\n");
            File.WriteAllText(P("f3.txt"), Lines(30, i => "M" + i));
            Directory.CreateDirectory(P("sub"));
            File.WriteAllText(P("sub/s.txt"), Lines(10, i => "S" + i));
            Run("add f1.txt f2.txt f3.txt sub");
            Run("commit -m base");
            Run($"copy \"{Url}/trunk\" \"{Url}/branches/feat\" -m branch", Root);
            Run($"copy \"{Url}/trunk\" \"{Url}/branches/other\" -m other-branch", Root);
            var bwc = Path.Combine(Root, "bwc");
            var owc = Path.Combine(Root, "owc");
            Run($"checkout \"{Url}/branches/feat\" \"{bwc}\"", Root);
            Run($"checkout \"{Url}/branches/other\" \"{owc}\"", Root);
            for (var n = 1; n <= count; n++)
            {
                File.AppendAllText(Path.Combine(bwc, "f2.txt"), $"b{n}\n");
                var f3 = File.ReadAllLines(Path.Combine(bwc, "f3.txt"));
                f3[n - 1] = $"M{n} B{n}";
                File.WriteAllText(Path.Combine(bwc, "f3.txt"), string.Join("\n", f3) + "\n");
                File.AppendAllText(Path.Combine(bwc, "sub", "s.txt"), n % 2 == 0 ? $"s{n}\n" : string.Empty);
                if (n == conflictAt)
                {
                    var f1 = File.ReadAllLines(Path.Combine(bwc, "f1.txt"));
                    f1[11] = "L12 BRANCH";
                    File.WriteAllText(Path.Combine(bwc, "f1.txt"), string.Join("\n", f1) + "\n");
                }

                Run($"commit -m \"feat {n}\"", bwc);
                FeatRevs.Add(long.Parse(Run("info --show-item last-changed-revision f2.txt", bwc).Trim()));
                File.AppendAllText(Path.Combine(owc, "f2.txt"), $"o{n}\n");
                Run($"commit -m \"other {n}\"", owc);
            }

            if (conflictAt > 0)
            {
                var f1 = File.ReadAllLines(P("f1.txt"));
                f1[11] = "L12 TRUNK";
                File.WriteAllText(P("f1.txt"), string.Join("\n", f1) + "\n");
                Run("commit -m \"trunk: stessa riga\"");
            }

            Run("update");
            Start();
        }

        /// <summary>(Ri)crea gli store sullo stesso svn e sugli stessi file di stato: e' come chiudere e riaprire Visual Studio.</summary>
        public void Start()
        {
            Changes?.Dispose();
            var real = new SvnService(SvnExe);
            (Svn, var spy) = SvnSpy.Wrap(real);
            if (Spy is not null)
                spy.Around = Spy.Around;
            Spy = spy;
            Changes = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())), recentTargetsFile: RecentFile)
            { WorkingCopyPath = Wc, TempRoot = Path.Combine(Root, "tmp"), Recent = new RecentMessages(Path.Combine(Root, "recentmsg.json")) };
            Log = new LogStore(Svn, Settings, Host, null, Changes);
            Changes.StartMerge = Log.StartMergeAsync;
            Changes.ContinueMerge = (origin, ct) => Log.Merge?.ResumeAsync(origin, ct) ?? Task.CompletedTask;
            Changes.MergeUndone = ct => Log.Merge!.RefreshAsync(ct);
        }

        public Task Cmd(string name, object? args = null) => Changes.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public Task LogCmd(string name, object? args = null) => Log.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);

        public ChangesState ChangesSnap => (ChangesState)Changes.GetSnapshot();

        public LogState LogSnap => (LogState)Log.GetSnapshot();

        public MergeDto Merge => LogSnap.Merge!;

        public async Task OpenMergeAsync(string? folder = null)
        {
            await Changes.RefreshAsync(Ct);
            await Cmd("mergeOpen", folder is null ? new { } : new { path = P(folder) });
            await Cmd("switchOk", new { name = "branches/feat" });
        }

        public string Eligible() => Run($"mergeinfo --show-revs eligible \"{Url}/branches/feat\"").Replace("\r", "").Replace("\n", " ").Trim();

        public string Tree()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.EnumerateFiles(Wc, "*", SearchOption.AllDirectories)
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".svn" + Path.DirectorySeparatorChar) && !f.Contains(".svn-assist")).Order())
                sb.AppendLine($"{Path.GetRelativePath(Wc, f).Replace('\\', '/')} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}");
            return sb.ToString();
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
    public async Task SvnVero_ConflittoAllaTerzaDiSette_Stop_StatoSalvato_Riapertura_Resolve_Continua_StessoRisultatoDiUnMergeUnico()
    {
        using var e = new Env();
        MergeStore.FilesDebounce = TimeSpan.Zero;
        // riferimento: un merge unico (conflitto risolto con "la loro") su una copia della working copy di partenza
        var refWc = Path.Combine(e.Root, "ref");
        e.Run($"checkout \"{e.Url}/trunk\" \"{refWc}\"", e.Root);
        e.Run($"merge --accept theirs-full \"{e.Url}/branches/feat\"", refWc);

        await e.OpenMergeAsync();
        Assert.Equal(7, e.Merge.Eligible);
        await e.LogCmd("mergeAll");

        // 1) si e' fermato DOPO la 3a revisione (in conflitto) e non ha toccato le successive
        var (r1, r2, r3, r4) = (e.FeatRevs[0], e.FeatRevs[1], e.FeatRevs[2], e.FeatRevs[3]);
        Assert.Contains($"b3", File.ReadAllText(e.P("f2.txt")));
        Assert.DoesNotContain("b4", File.ReadAllText(e.P("f2.txt")));
        Assert.Equal(string.Join(" ", e.FeatRevs.Skip(3).Select(r => "r" + r)), e.Eligible());   // svn sa gia' cosa manca
        var pending = e.ChangesSnap.Merge!.Pending!;
        Assert.Equal((3, 7, 4), (pending.Applied, pending.Total, pending.Remaining));
        Assert.Equal([r1, r2, r3], pending.AppliedRevs);
        Assert.Equal(e.FeatRevs.Skip(3), pending.RemainingRevs);
        Assert.Equal(r3, pending.ConflictRev);
        Assert.Equal($"Applicate 3 di 7: r{r1}, r{r2}, r{r3}; ferma alla r{r3} per conflitto in 1 file.", pending.AppliedText);
        Assert.StartsWith("Rimaste 4: ", pending.RemainingText);
        Assert.Equal(["Risolvi i conflitti nei file elencati qui sotto (Usa la mia / Usa la loro / Usa entrambe / editor di merge).", "Segna ogni file come risolto."], pending.Steps.Take(2));
        Assert.Contains("Continua merge", pending.Steps[2]);
        Assert.Contains("Annulla merge", pending.Alternative);
        Assert.False(pending.CanContinue);
        Assert.Contains("1 file in conflitto", pending.WhyNot);
        Assert.Equal("", e.Merge.Error);                                           // una sola spiegazione, nessun errore in inglese
        // il Log in modalita' merge mostra lo stesso stato e le applicate (3) non sono tra quelle da unire
        Assert.Equal(pending.AppliedText, e.Merge.Interrupted!.AppliedText);
        Assert.Equal(4, e.LogSnap.Entries.Count);
        // passaggi: un solo intervallo per le prime due (r1-r2), poi la 3a da sola (sospetta: tocca il file in conflitto previsto dal dry-run)
        Assert.Equal([$"{r1}-{r2}", $"{r3}"], e.Spy.Merges(real: true).Select(c => c["merge ".Length..]));
        Assert.Single(e.Spy.Merges(real: false));

        // 2) stato su disco, poi "chiudi e riapri Visual Studio": nuovi store sullo stesso file, il banner ricompare
        Assert.True(File.Exists(e.RecentFile + ".pending-merge.json"));
        e.Start();
        await e.Changes.RefreshAsync(Ct, reloadBranch: true);
        var again = e.ChangesSnap.Merge!.Pending!;
        Assert.Equal(pending.AppliedText, again.AppliedText);
        Assert.Equal(pending.RemainingRevs, again.RemainingRevs);
        Assert.False(again.CanContinue);
        Assert.False(again.Incompatible);

        // 3) "Continua merge" con il conflitto ancora aperto: rifiutato, niente svn merge
        var before = e.Spy.Merges(real: true).Count;
        await e.Cmd("mergeContinue");
        Assert.Equal(before, e.Spy.Merges(real: true).Count);
        Assert.Contains("file in conflitto", e.ChangesSnap.Status.Msg);

        // 4) risolvi (la loro), poi Continua: solo le 4 rimaste, numerazione sull'intero merge
        await e.Cmd("conflictOp", new { op = "theirs", key = "changes:f1.txt" });
        Assert.True(e.ChangesSnap.Merge!.Pending!.CanContinue);
        var progressSeen = new List<MergeStepDto>();
        e.Spy.Around = (m, a, run) => { if (m.Name == "MergeRangesAsync" && !(bool)a[3]! && e.Changes.Progress.GetSnapshot()?.Step is { } s) lock (progressSeen) progressSeen.Add(s); return run(); };   // il passo e' scritto prima di ogni chiamata a svn
        await e.Cmd("mergeContinue");

        Assert.Equal([$"{r4}-{e.FeatRevs[6]}"], e.Spy.Merges(real: true).Skip(before).Select(c => c["merge ".Length..]));   // un intervallo con le 4 rimaste (la 7a e' l'ultima)
        Assert.Equal("", e.Eligible());                                              // tutto unito
        Assert.Null(e.ChangesSnap.Merge!.Pending);                                   // concluso: niente piu' "interrotto"
        Assert.False(File.Exists(e.RecentFile + ".pending-merge.json") && File.ReadAllText(e.RecentFile + ".pending-merge.json").Contains("branches/feat"));
        Assert.Contains(progressSeen, s => s.Total == 7 && s.First == 4);            // "Applico r.. (4 di 7)" fin dalla ripresa
        Assert.Equal(e.Tree(), TreeOf(refWc));                                       // stesso risultato di un merge unico
        // messaggio di commit con TUTTE le revisioni del merge, non solo l'ultimo tratto
        var message = e.Changes.Recent!.Get(e.Url.Replace("file:///", "file:///"))?.FirstOrDefault() ?? e.ChangesSnap.Commit.Recent!.First();
        Assert.StartsWith("Unite le revisioni ", message);
        foreach (var r in e.FeatRevs)
            Assert.Contains($"r{r} |", message);
        Assert.Equal("", e.Eligible());
    }

    [Fact]
    public async Task SvnVero_ConflittoSuRevisioniSelezionateNonAdiacenti_ChangesMostraIlConflitto_NonNessunaModifica()
    {
        using var e = new Env();
        MergeStore.FilesDebounce = TimeSpan.Zero;
        await e.OpenMergeAsync();
        await e.LogCmd("mergeToggle", new { rev = e.FeatRevs[2] });
        await e.LogCmd("mergeToggle", new { rev = e.FeatRevs[3] });
        await e.LogCmd("mergeSelected");
        await e.Changes.RefreshAsync(Ct);   // come il refresh del pannello
        var snap = e.ChangesSnap;
        Assert.NotEmpty(snap.Conflicts ?? []);
        Assert.NotEmpty(snap.Sections);
        Assert.DoesNotContain("Nessuna modifica", snap.Status.Msg);
    }

    private static string TreeOf(string wc)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in Directory.EnumerateFiles(wc, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".svn" + Path.DirectorySeparatorChar) && !f.Contains(".svn-assist")).Order())
            sb.AppendLine($"{Path.GetRelativePath(wc, f).Replace('\\', '/')} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}");
        return sb.ToString();
    }

    [Fact]
    public async Task SvnVero_SenzaConflitti_UnSoloIntervalloPerTutteLeRevisioni_NonUnaChiamataPerRevisione()
    {
        using var e = new Env(count: 30, conflictAt: 0);
        await e.OpenMergeAsync();
        MergeRunner.ChunkSize = 12;   // 30 revisioni a gruppi da 12: 3 intervalli (e non 30 chiamate a svn)
        try { await e.LogCmd("mergeAll"); }
        finally { MergeRunner.ChunkSize = MergeRunner.DefaultChunkSize; }

        Assert.Equal(3, e.Spy.Merges(real: true).Count);
        Assert.Single(e.Spy.Merges(real: false));   // un dry-run solo (finestra da 200)
        Assert.Equal("", e.Eligible());
        Assert.Null(e.ChangesSnap.Merge!.Pending);
        Assert.Contains("rivedi", e.ChangesSnap.Status.Msg);
        Assert.Equal(30, e.FeatRevs.Count);
    }

    [Fact]
    public async Task SvnVero_AnnullaMorbido_FermaAFinePassaggio_ContinuaApplicaLeRimaste()
    {
        using var e = new Env(count: 30, conflictAt: 0);
        await e.OpenMergeAsync();
        MergeRunner.ChunkSize = 10;
        // "Annulla" premuto mentre gira il 1o passaggio: si ferma alla fine di quel passaggio, senza terminare svn
        var pressed = false;
        e.Spy.Around = (m, args, run) =>
        {
            if (m.Name == "MergeRangesAsync" && !(bool)args[3]! && !pressed)
            {
                pressed = true;
                Assert.True(e.Changes.Progress.Cancel());
            }

            return run();
        };
        try { await e.LogCmd("mergeAll"); }
        finally { MergeRunner.ChunkSize = MergeRunner.DefaultChunkSize; }

        Assert.Single(e.Spy.Merges(real: true));
        var pending = e.ChangesSnap.Merge!.Pending!;
        Assert.Equal((10, 30, 20), (pending.Applied, pending.Total, pending.Remaining));
        Assert.Equal("", pending.Warning);                                         // nulla a meta': il passaggio e' finito
        Assert.True(pending.CanContinue);
        Assert.Equal("interrupted", pending.Kind);
        Assert.Equal(["Premi \"Continua merge\": vengono applicate solo le 20 revisioni rimaste (r" + e.FeatRevs[10] + "…r" + e.FeatRevs[29] + "); non serve rifare la selezione né un merge completo."], pending.Steps);
        var progress = e.LogSnap.Progress!;
        Assert.True(progress.Cancelled);
        Assert.Equal(["mergeContinue", "progressRevertMerge", "progressClose"], progress.Actions!.Select(a => a.Cmd));

        MergeRunner.ChunkSize = 10;
        try { await e.Cmd("mergeContinue"); }
        finally { MergeRunner.ChunkSize = MergeRunner.DefaultChunkSize; }

        Assert.Equal("", e.Eligible());
        Assert.Null(e.ChangesSnap.Merge!.Pending);
        Assert.Equal(3, e.Spy.Merges(real: true).Count);                           // 10 + 10 + 10: le prime 10 non sono state rifatte
    }

    [Fact]
    public async Task SvnVero_ProcessoTerminatoAMeta_CleanupAutomatico_Continua_PoiAnnullaRipristinaOgniCosa()
    {
        using var e = new Env(count: 20, conflictAt: 0);
        File.AppendAllText(e.P("f1.txt"), "MIA modifica pendente\n");               // modifica pendente (file che il branch non tocca): il merge salva una copia di sicurezza
        var before = e.Tree();
        await e.OpenMergeAsync();
        MergeRunner.ChunkSize = 5;
        // il 2o passaggio: svn applica i file ma il processo viene terminato prima di registrare mergeinfo e di restituire (come una chiusura di VS)
        var real = 0;
        e.Spy.Around = (m, args, run) =>
        {
            if (m.Name == "MergeRangesAsync" && !(bool)args[3]!)
            {
                real++;
                if (real == 2)
                {
                    ((Task)run()!).GetAwaiter().GetResult();
                    e.Run("revert --depth empty .");                                // niente mergeinfo registrato per questo passaggio
                    e.Changes.Progress.Cancel();
                    throw new OperationCanceledException();
                }
            }

            return run();
        };
        try { await e.LogCmd("mergeAll"); }
        finally { MergeRunner.ChunkSize = MergeRunner.DefaultChunkSize; }

        Assert.Contains(e.Spy.Calls, c => c == "Cleanup");                          // cleanup automatico dopo il processo terminato
        var pending = e.ChangesSnap.Merge!.Pending!;
        Assert.Equal((5, 20, 15), (pending.Applied, pending.Total, pending.Remaining));
        Assert.Contains("terminato mentre applicava", pending.Warning);             // avviso sul passaggio a meta'
        Assert.True(e.ChangesSnap.Merge.Pending!.HasBackup);

        // riapertura di VS: stato e copia di sicurezza riletti da disco
        e.Start();
        e.Spy.Around = null;
        await e.Changes.RefreshAsync(Ct, reloadBranch: true);
        Assert.Equal(5, e.ChangesSnap.Merge!.Pending!.Applied);

        // "Annulla merge": toglie tutto il merge (anche il passaggio a meta') e rimette la modifica pendente dell'utente, byte per byte
        await e.Cmd("mergeUndo");
        Assert.Equal(before, e.Tree());
        Assert.Null(e.ChangesSnap.Merge);
        Assert.False(File.Exists(e.RecentFile + ".pending-merge.json") && File.ReadAllText(e.RecentFile + ".pending-merge.json").Contains("branches/feat"));
        Assert.Equal(20, e.Eligible().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);   // tornano tutte da unire
        Assert.Empty(Directory.GetDirectories(PatchBackup.BackupsFolder(e.Wc), "*-pre-merge"));
    }

    [Fact]
    public async Task SvnVero_StatoRunningDopoCrash_AllaRiaperturaCleanupEBanner()
    {
        using var e = new Env(count: 6, conflictAt: 0);
        await e.OpenMergeAsync();
        var info = e.Run("info --show-item revision").Trim();
        // VS e' crashato durante il 1o passaggio: stato "in corso" con il passaggio segnato
        e.Changes.PendingMerges.Set(e.Wc, new PendingMerge.Entry("branches/feat", e.Url + "/branches/feat", "branches/feat", e.Wc, null, e.Url + "/trunk", long.Parse(info),
            e.FeatRevs.ToList(), [], PendingMerge.Running, DateTime.Now, DateTime.Now, InFlightFrom: e.FeatRevs[0], InFlightTo: e.FeatRevs[1]));
        e.Start();
        await e.Changes.RefreshAsync(Ct, reloadBranch: true);

        Assert.Contains(e.Spy.Calls, c => c == "Cleanup");
        var pending = e.ChangesSnap.Merge!.Pending!;
        Assert.Equal("interrupted", pending.Kind);
        Assert.Equal((0, 6, 6), (pending.Applied, pending.Total, pending.Remaining));
        Assert.Contains("terminato mentre applicava", pending.Warning);
        Assert.True(pending.CanContinue);

        await e.Cmd("mergeContinue");   // riprende dalle revisioni rimaste (tutte): il merge si conclude
        Assert.Equal("", e.Eligible());
        Assert.Null(e.ChangesSnap.Merge!.Pending);
    }

    [Fact]
    public async Task SvnVero_WorkingCopyAggiornataNelFrattempo_NonSiContinuaAllaCieca_SoloAnnullaEScarta()
    {
        using var e = new Env(count: 6, conflictAt: 3);
        await e.OpenMergeAsync();
        await e.LogCmd("mergeAll");
        Assert.NotNull(e.ChangesSnap.Merge!.Pending);

        // un altro utente committa e l'utente fa SVN Update: la revisione della working copy cambia
        var other = Path.Combine(e.Root, "other-trunk");
        e.Run($"checkout \"{e.Url}/trunk\" \"{other}\"", e.Root);
        File.AppendAllText(Path.Combine(other, "f3.txt"), "ultima riga\n");
        e.Run("commit -m \"altro utente\"", other);
        e.Start();
        e.Run("update");
        await e.Changes.RefreshAsync(Ct, reloadBranch: true);

        var pending = e.ChangesSnap.Merge!.Pending!;
        Assert.True(pending.Incompatible);
        Assert.False(pending.CanContinue);
        Assert.Contains("aggiornata", pending.WhyNot);
        var calls = e.Spy.Merges(real: true).Count;
        await e.Cmd("mergeContinue");
        Assert.Equal(calls, e.Spy.Merges(real: true).Count);                         // mai un merge alla cieca
        Assert.Contains("non si può più continuare", e.ChangesSnap.Status.Msg + e.Merge?.Error);

        await e.Cmd("mergeDiscard");                                                 // Scarta: lo stato sparisce, le modifiche applicate restano
        Assert.Null(e.ChangesSnap.Merge?.Pending);
        Assert.Contains("b1", File.ReadAllText(e.P("f2.txt")));
    }

    [Fact]
    public async Task SvnVero_MergeDiUnaCartella_ConflittoEContinua_RestaNellaCartella()
    {
        using var e = new Env(count: 6, conflictAt: 0);
        e.Dialogs.AskResult = true;   // "Compatibile: continuare?" del merge di una cartella
        // un conflitto dentro sub/: stessa riga di s.txt modificata sul branch e su trunk, seguita da altre due revisioni del branch su sub/
        var bwc = Path.Combine(e.Root, "bwc");
        File.WriteAllText(Path.Combine(bwc, "sub", "s.txt"), string.Join("\n", Enumerable.Range(1, 10).Select(i => i == 5 ? "S5 BRANCH" : "S" + i)) + "\ns2\ns4\ns6\n");
        e.Run("commit -m \"feat sub\"", bwc);
        File.AppendAllText(Path.Combine(bwc, "sub", "s.txt"), "dopo1\n");
        e.Run("commit -m \"feat sub dopo 1\"", bwc);
        File.AppendAllText(Path.Combine(bwc, "sub", "s.txt"), "dopo2\n");
        e.Run("commit -m \"feat sub dopo 2\"", bwc);
        File.WriteAllText(e.P("sub/s.txt"), string.Join("\n", Enumerable.Range(1, 10).Select(i => i == 5 ? "S5 TRUNK" : "S" + i)) + "\n");
        e.Run("commit -m \"trunk sub\"");
        e.Run("update");
        await e.OpenMergeAsync(folder: "sub");
        Assert.Equal("branches/feat/sub", e.Merge.Source);
        await e.LogCmd("mergeAll");

        var pending = e.ChangesSnap.Merge!.Pending!;
        Assert.Equal(2, pending.Remaining);                                          // le due dopo il conflitto
        var state = e.Changes.CurrentPendingMerge!;
        Assert.Equal("sub", state.Sub);                                              // il merge e' della cartella
        Assert.Equal(e.P("sub"), state.Wc.TrimEnd('\\', '/'));
        Assert.Contains(e.ChangesSnap.Conflicts ?? [], c => c.Path == "sub/s.txt");
        e.Start();                                                                  // riapertura
        await e.Changes.RefreshAsync(Ct, reloadBranch: true);
        Assert.Equal("sub", e.Changes.MergeSubPath);                                 // l'annullamento resta nella cartella
    }
}
