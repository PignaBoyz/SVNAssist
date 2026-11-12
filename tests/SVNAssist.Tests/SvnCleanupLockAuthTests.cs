using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Cleanup, Update to revision, Lock/Unlock e login SVN (retry dopo errore di autenticazione).</summary>
public class SvnCleanupLockAuthTests
{
    private const string FakeSvnPath = @"C:\fake\svn.exe";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed record Call(IReadOnlyList<string> Args, string? Dir, string? Stdin);

    private sealed class Runner : IProcessRunner
    {
        public List<Call> Calls { get; } = [];
        public Func<IReadOnlyList<string>, int, ProcessResult> Respond { get; set; } = (_, _) => new ProcessResult(0, "", "");

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(new Call(arguments, workingDirectory, standardInput));
            return Task.FromResult(arguments.Contains("--version") ? new ProcessResult(0, "1.14.5\n", "") : Respond(arguments, Calls.Count));
        }
    }

    private sealed class Prompt(SvnCredentials? answer) : ISvnCredentialPrompt
    {
        public int Calls { get; private set; }
        public Task<SvnCredentials?> RequestAsync(string detail, CancellationToken ct) { Calls++; return Task.FromResult(answer); }
    }

    private const string AuthError = "svn: E170001: Authentication failed\nsvn: E215004: No more credentials or we tried too many times.";

    // ── Funzioni pure ────────────────────────────────────────────────

    [Theory]
    [InlineData("svn: E170001: Authorization failed", true)]
    [InlineData("svn: E215004: No more credentials or we tried too many times.\nAuthentication failed", true)]
    [InlineData("svn: E230001: Server SSL certificate verification failed", false)]
    [InlineData("svn: E170013: Unable to connect to a repository", false)]
    [InlineData("svn: E155004: Working copy locked", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAuthenticationError(string? text, bool expected) => Assert.Equal(expected, SvnErrors.IsAuthenticationError(text));

    [Theory]
    [InlineData("svn: E155004: Run 'svn cleanup' to remove locks (type 'svn help cleanup' for details)", true)]
    [InlineData("svn: E155037: Previous operation has not finished; run 'cleanup' if it was interrupted", true)]
    [InlineData("svn: E170001: Authentication failed", false)]
    public void IsWorkingCopyLocked(string text, bool expected) => Assert.Equal(expected, SvnErrors.IsWorkingCopyLocked(text));

    [Theory]
    [InlineData("svn: warning: W160035: Path '/trunk/a.txt' is already locked by user 'bob'", true)]
    [InlineData("svn: E200035: Path is already locked", true)]
    [InlineData("svn: E195013: 'a.txt' is not locked in this working copy", false)]
    public void IsLockedByOther(string text, bool expected) => Assert.Equal(expected, SvnErrors.IsLockedByOther(text));

    [Theory]
    [InlineData("1234", "1234", true)]
    [InlineData(" 007 ", "7", true)]
    [InlineData("head", "HEAD", true)]
    [InlineData("HEAD", "HEAD", true)]
    [InlineData("0", "", false)]
    [InlineData("-5", "", false)]
    [InlineData("12abc", "", false)]
    [InlineData("1.5", "", false)]
    [InlineData("r12", "", false)]
    [InlineData("", "", false)]
    [InlineData("99999999999999999999", "", false)]
    [InlineData(null, "", false)]
    public void RevisionValidation(string? input, string expected, bool valid)
    {
        Assert.Equal(valid, SvnRevision.TryNormalize(input, out var rev));
        Assert.Equal(expected, rev);
    }

    [Theory]
    [InlineData("1.14.5", true)]
    [InlineData("1.10.0\r\n", true)]
    [InlineData("2.0.0", true)]
    [InlineData("1.9.7", false)]
    [InlineData("boh", false)]
    public void PasswordFromStdinNeedsSvn110(string version, bool expected) => Assert.Equal(expected, SvnService.SupportsPasswordFromStdin(version));

    // ── Argomenti dei comandi ────────────────────────────────────────

    [Fact]
    public async Task UpdateToRevision_PassaDashR_EValidaLaRevisione()
    {
        var r = new Runner { Respond = (_, _) => new ProcessResult(0, "Updated to revision 42.\n", "") };
        var sut = new SvnService(FakeSvnPath, r);

        var result = await sut.UpdateToRevisionAsync(@"C:\wc", "42", Ct);

        Assert.Equal(42, result.Revision);
        Assert.Equal(["update", "--non-interactive", "-r", "42"], r.Calls.Single().Args);
        Assert.Equal(@"C:\wc", r.Calls.Single().Dir);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.UpdateToRevisionAsync(@"C:\wc", "abc", Ct));
        await sut.UpdateToRevisionAsync(@"C:\wc", "head", Ct);
        Assert.Equal(["update", "--non-interactive", "-r", "HEAD"], r.Calls.Last().Args);
        Assert.Equal(2, r.Calls.Count); // la revisione non valida non ha lanciato svn
    }

    [Fact]
    public async Task Cleanup_EseguiNellaCartella()
    {
        var r = new Runner();
        await new SvnService(FakeSvnPath, r).CleanupAsync(@"C:\wc", Ct);

        Assert.Equal(["cleanup", "--non-interactive"], r.Calls.Single().Args);
        Assert.Equal(@"C:\wc", r.Calls.Single().Dir);
    }

    [Fact]
    public async Task Lock_ConMessaggioForceESenza()
    {
        string? content = null;
        var r = new Runner
        {
            Respond = (a, _) =>
            {
                var i = a.ToList().IndexOf("-F");
                if (i >= 0) content = File.ReadAllText(a[i + 1]);
                return new ProcessResult(0, "", "");
            },
        };
        var sut = new SvnService(FakeSvnPath, r);

        await sut.LockAsync([@"C:\wc\a.cs"], "tocco io: \u2192 modifica", force: false, Ct);
        Assert.Equal("lock", r.Calls[0].Args[0]);
        Assert.Contains("--encoding", r.Calls[0].Args);
        Assert.DoesNotContain("--force", r.Calls[0].Args);
        Assert.Equal(@"C:\wc\a.cs", r.Calls[0].Args[^1]);
        Assert.Equal("tocco io: \u2192 modifica", content);
        Assert.DoesNotContain(r.Calls[0].Args, x => x.Contains("tocco")); // il messaggio non va in riga di comando

        await sut.LockAsync([@"C:\wc\a.cs"], "", force: true, Ct);
        Assert.Equal(["lock", "--non-interactive", "--force", @"C:\wc\a.cs"], r.Calls[1].Args);
    }

    [Fact]
    public async Task Unlock_ArgomentiENessunPath()
    {
        var r = new Runner();
        var sut = new SvnService(FakeSvnPath, r);
        await sut.UnlockAsync([@"C:\wc\a.cs"], Ct);
        await sut.UnlockAsync([], Ct);

        Assert.Equal(["unlock", "--non-interactive", @"C:\wc\a.cs"], r.Calls.Single().Args);
    }

    [Fact]
    public void StatusXml_LockLocale_ImpostaIsLocked()
    {
        const string xml = """
            <status><target path="."><entry path="a.txt"><wc-status item="normal" revision="2" props="none">
            <lock><token>opaquelocktoken:1</token><owner>andre</owner><created>2026-01-01T00:00:00Z</created></lock>
            </wc-status></entry><entry path="b.txt"><wc-status item="modified" props="none"/></entry></target></status>
            """;
        var list = SvnService.ParseStatusXml(xml);

        Assert.True(list.Single(s => s.Path == "a.txt").IsLocked);
        Assert.Equal(SvnStatusKind.Normal, list.Single(s => s.Path == "a.txt").Status);
        Assert.False(list.Single(s => s.Path == "b.txt").IsLocked);
    }

    // ── Login SVN ────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_ErroreDiAutenticazione_ChiedeCredenzialiERipeteUnaVoltaConStdin()
    {
        var r = new Runner { Respond = (_, n) => n == 1 ? new ProcessResult(1, "", AuthError) : new ProcessResult(0, "Updated to revision 5.\n", "") };
        var prompt = new Prompt(new SvnCredentials("bob", "s3gr3to", Remember: true));
        var sut = new SvnService(FakeSvnPath, r, credentials: prompt);

        var result = await sut.UpdateAsync(@"C:\wc", Ct);

        Assert.Equal(5, result.Revision);
        Assert.Equal(1, prompt.Calls);
        var retry = r.Calls.Last();
        Assert.Equal(["update", "--non-interactive", "--username", "bob", "--password-from-stdin"], retry.Args);
        Assert.Equal("s3gr3to\n", retry.Stdin);
        Assert.DoesNotContain(r.Calls.SelectMany(c => c.Args), a => a.Contains("s3gr3to"));
        Assert.Null(r.Calls[0].Stdin);
    }

    [Fact]
    public async Task Auth_NonRicorda_AggiungeNoAuthCache()
    {
        var r = new Runner { Respond = (_, n) => n == 1 ? new ProcessResult(1, "", AuthError) : new ProcessResult(0, "At revision 5.\n", "") };
        var sut = new SvnService(FakeSvnPath, r, credentials: new Prompt(new SvnCredentials("bob", "pw", Remember: false)));

        await sut.UpdateAsync(@"C:\wc", Ct);

        Assert.Contains("--no-auth-cache", r.Calls.Last().Args);
    }

    [Fact]
    public async Task Auth_RiproviSoloUnaVolta_EPasswordMaiNelMessaggio()
    {
        var r = new Runner { Respond = (_, _) => new ProcessResult(1, "", AuthError) };
        var prompt = new Prompt(new SvnCredentials("bob", "s3gr3to", true));
        var sut = new SvnService(FakeSvnPath, r, credentials: prompt);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateAsync(@"C:\wc", Ct));

        Assert.Equal(1, prompt.Calls);
        Assert.Equal(3, r.Calls.Count); // primo tentativo, svn --version, un solo retry
        Assert.DoesNotContain("s3gr3to", ex.Message);
        Assert.DoesNotContain("s3gr3to", new SvnCredentials("bob", "s3gr3to", true).ToString());
    }

    [Fact]
    public async Task Auth_Annullato_ErroreNormaleSenzaRetry()
    {
        var r = new Runner { Respond = (_, _) => new ProcessResult(1, "", AuthError) };
        var prompt = new Prompt(null);
        var sut = new SvnService(FakeSvnPath, r, credentials: prompt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateAsync(@"C:\wc", Ct));

        Assert.Equal(1, prompt.Calls);
        Assert.Single(r.Calls);
    }

    [Fact]
    public async Task Auth_ErroreNonDiAutenticazione_NienteDialogo()
    {
        var r = new Runner { Respond = (_, _) => new ProcessResult(1, "", "svn: E170013: Unable to connect to a repository") };
        var prompt = new Prompt(new SvnCredentials("bob", "pw", true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SvnService(FakeSvnPath, r, credentials: prompt).UpdateAsync(@"C:\wc", Ct));

        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task Auth_SenzaProviderDiCredenziali_ErroreNormale()
    {
        var r = new Runner { Respond = (_, _) => new ProcessResult(1, "", AuthError) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SvnService(FakeSvnPath, r).UpdateAsync(@"C:\wc", Ct));

        Assert.Single(r.Calls);
    }

    [Fact]
    public async Task Auth_SvnVecchio_UsaPasswordInRigaDiComando()
    {
        var r = new Runner();
        r.Respond = (_, n) => n == 1 ? new ProcessResult(1, "", AuthError) : new ProcessResult(0, "At revision 5.\n", "");
        var old = new OldSvnRunner(r);
        var sut = new SvnService(FakeSvnPath, old, credentials: new Prompt(new SvnCredentials("bob", "pw", true)));

        await sut.UpdateAsync(@"C:\wc", Ct);

        var retry = r.Calls.Last();
        Assert.Contains("--password", retry.Args);
        Assert.DoesNotContain("--password-from-stdin", retry.Args);
        Assert.Null(retry.Stdin);
    }

    /// <summary>Come <see cref="Runner"/> ma <c>--version</c> risponde 1.9.7.</summary>
    private sealed class OldSvnRunner(Runner inner) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
            => arguments.Contains("--version")
                ? Task.FromResult(new ProcessResult(0, "1.9.7\n", ""))
                : inner.RunAsync(fileName, arguments, workingDirectory, ct, standardInput, environment);
    }

    // ── Store: lock, unlock, cleanup, update to revision ─────────────

    private sealed class Fx : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public FakeHost Host { get; } = new();
        public StubSettings Settings { get; } = new();
        public ChangesStore Store { get; }

        public Fx()
        {
            Store = new ChangesStore(Settings, Svn, new FakeAiFactory(), new NoopAiPrompt(), Dialogs, Host,
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())))
            { WorkingCopyPath = @"C:\wc", TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_LK_" + Guid.NewGuid().ToString("N")[..8]) };
        }

        public ChangesState Snap => (ChangesState)Store.GetSnapshot();
        public RowDto Row(string key) => Snap.Sections.SelectMany(s => s.Rows).Single(r => r.Key == key);
        public Task Cmd(string name, object? args = null) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(args ?? new { }), Ct);
        public void Dispose() => Store.Dispose();
    }

    [Fact]
    public async Task Store_FileBloccatoEFileSoloBloccato_BadgeEAzioni()
    {
        using var f = new Fx();
        f.Svn.Statuses.Add(new SvnFileStatus("m.cs", SvnStatusKind.Modified));
        f.Svn.Statuses.Add(new SvnFileStatus("mine.cs", SvnStatusKind.Modified, IsLocked: true));
        f.Svn.Statuses.Add(new SvnFileStatus("k.cs", SvnStatusKind.Normal, IsLocked: true));
        f.Svn.Statuses.Add(new SvnFileStatus("plain.cs", SvnStatusKind.Normal)); // non bloccato: non compare
        await f.Store.RefreshAsync(Ct);

        Assert.True(f.Row("staged:m.cs").Can.Lock);
        Assert.False(f.Row("staged:m.cs").Can.Unlock);
        Assert.Null(f.Row("staged:m.cs").Locked);

        var mine = f.Row("staged:mine.cs");
        Assert.True(mine.Locked);
        Assert.True(mine.Can.Unlock);
        Assert.False(mine.Can.Lock);

        var k = f.Row("changes:k.cs");
        Assert.Equal("K", k.Status);
        Assert.True(k.Locked);
        Assert.False(k.Can.Stage);
        Assert.False(k.Can.Diff);
        Assert.False(k.Can.Revert);
        Assert.DoesNotContain(f.Snap.Sections.SelectMany(s => s.Rows), r => r.Key.EndsWith("plain.cs"));
        Assert.Contains("1 bloccati", f.Snap.Status.Msg);
    }

    [Fact]
    public async Task Store_Lock_ChiedeMessaggioEBlocca_UnlockSblocca()
    {
        using var f = new Fx();
        f.Svn.Statuses.Add(new SvnFileStatus("m.cs", SvnStatusKind.Modified));
        await f.Store.RefreshAsync(Ct);
        f.Dialogs.TextResult = "lavoro io";

        await f.Cmd("rowOp", new { op = "lock", key = "staged:m.cs" });

        var (paths, message, force) = f.Svn.Locked.Single();
        Assert.Equal([Path.Combine(@"C:\wc", "m.cs")], paths);
        Assert.Equal("lavoro io", message);
        Assert.False(force);

        f.Svn.Statuses.Clear();
        f.Svn.Statuses.Add(new SvnFileStatus("m.cs", SvnStatusKind.Modified, IsLocked: true));
        await f.Store.RefreshAsync(Ct);
        await f.Cmd("rowOp", new { op = "unlock", key = "staged:m.cs" });

        Assert.Single(f.Svn.Unlocked);
    }

    [Fact]
    public async Task Store_Lock_Annullato_NonBlocca()
    {
        using var f = new Fx();
        f.Svn.Statuses.Add(new SvnFileStatus("m.cs", SvnStatusKind.Modified));
        await f.Store.RefreshAsync(Ct);
        f.Dialogs.TextResult = null;

        await f.Cmd("rowOp", new { op = "lock", key = "staged:m.cs" });

        Assert.Empty(f.Svn.Locked);
    }

    [Fact]
    public async Task Store_Lock_GiaDiUnAltro_RubaSoloConConfermaEsplicita()
    {
        using var f = new Fx();
        f.Svn.Statuses.Add(new SvnFileStatus("m.cs", SvnStatusKind.Modified));
        await f.Store.RefreshAsync(Ct);
        f.Svn.LockErrorUnlessForced = "svn: warning: W160035: Path '/trunk/m.cs' is already locked by user 'bob'";

        f.Dialogs.ConfirmResult = false;
        await f.Cmd("rowOp", new { op = "lock", key = "staged:m.cs" });
        Assert.Empty(f.Svn.Locked);
        Assert.Contains("bloccato da un altro", f.Dialogs.Messages.Last());

        f.Dialogs.ConfirmResult = true;
        await f.Cmd("rowOp", new { op = "lock", key = "staged:m.cs" });
        Assert.True(f.Svn.Locked.Single().Force);
    }

    [Fact]
    public async Task Store_UpdateToRevision_ApreLaScelta_ElencaLeRevisioni_CaricaAltre_OkEseguiUpdate()
    {
        using var f = new Fx();
        f.Svn.LogEntryCount = 100;

        await f.Cmd("updateToRevision");
        var p = f.Snap.RevPicker;
        Assert.True(p.Open);
        Assert.Equal(@"C:\wc", p.Target);
        Assert.Equal(RevisionPickerStore.PageSize, p.Items.Count);
        Assert.True(p.CanMore);
        Assert.Equal("m1", p.Items[0].Msg);
        Assert.Empty(f.Svn.UpdatedToRevision); // aprire la scelta non aggiorna nulla

        await f.Cmd("revPickerMore");
        Assert.Equal(2 * RevisionPickerStore.PageSize, f.Snap.RevPicker.Items.Count);

        await f.Cmd("revPickerOk", new { rev = "abc" });
        Assert.True(f.Snap.RevPicker.Open); // non valido: resta aperta, con l'errore
        Assert.Contains("non è una revisione valida", f.Snap.RevPicker.Error);
        Assert.Empty(f.Svn.UpdatedToRevision);

        await f.Cmd("revPickerOk", new { rev = "42" });
        Assert.False(f.Snap.RevPicker.Open);
        Assert.Equal([(@"C:\wc", "42")], f.Svn.UpdatedToRevision);
        Assert.Contains("42", f.Snap.Status.Msg);
    }

    [Fact]
    public async Task Store_UpdateToRevision_Annulla_NonAggiorna_HeadAccettato()
    {
        using var f = new Fx();
        await f.Cmd("updateToRevision");
        await f.Cmd("revPickerCancel");
        Assert.False(f.Snap.RevPicker.Open);
        Assert.Empty(f.Svn.UpdatedToRevision);

        await f.Cmd("updateToRevision");
        await f.Cmd("revPickerOk", new { rev = "head" });
        Assert.Equal([(@"C:\wc", "HEAD")], f.Svn.UpdatedToRevision);
    }

    [Fact]
    public async Task Store_UpdateToRevision_DaSolutionExplorer_AggiornaIlPercorsoScelto()
    {
        using var f = new Fx();
        await f.Store.OpenRevisionPickerAsync([@"C:\wc\sub"], Ct);
        Assert.Equal(@"C:\wc\sub", f.Snap.RevPicker.Target);

        await f.Cmd("revPickerOk", new { rev = "7" });

        Assert.Equal([(@"C:\wc\sub", "7")], f.Svn.UpdatedToRevision);
    }

    [Fact]
    public async Task Store_ShowLog_ApreIlLogDellaWorkingCopy()
    {
        using var f = new Fx();
        await f.Cmd("showLog");
        Assert.Equal([@"C:\wc"], f.Host.LogShown);
    }

    private sealed class QueueDialogs(Queue<string?> answers) : IDialogService
    {
        public int Asked { get; private set; }
        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(true);
        public Task<SvnConflictChoice?> ChooseConflictResolutionAsync(string fileName, CancellationToken ct) => Task.FromResult<SvnConflictChoice?>(null);
        public Task<string?> AskTextAsync(string message, string label, string defaultValue, CancellationToken ct) { Asked++; return Task.FromResult(answers.Dequeue()); }
    }

    [Fact]
    public async Task Store_Cleanup_EseguitoEMessaggioDiEsito()
    {
        using var f = new Fx();

        await f.Cmd("cleanup");

        Assert.Equal([@"C:\wc"], f.Svn.CleanedUp);
        Assert.Contains("Cleanup completato", f.Snap.Status.Msg);
    }

    [Fact]
    public async Task Store_WorkingCopyBloccata_StatoConPulsanteEseguiCleanup()
    {
        using var f = new Fx();
        f.Svn.UpdateError = "La working copy è bloccata.\nsvn update fallito (exit code 1): svn: E155004: Run 'svn cleanup'";

        await f.Cmd("update");

        var status = f.Snap.Status;
        Assert.True(status.Err);
        Assert.Equal("cleanup", status.Action?.Cmd);
        Assert.Equal("Esegui Cleanup", status.Action?.Label);

        await f.Cmd("cleanup");
        Assert.Null(f.Snap.Status.Action);
    }

    // ── Solution Explorer (PathCommands) ─────────────────────────────

    private sealed class PcFx : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SVNAssist_PC2_" + Guid.NewGuid().ToString("N")[..8]);
        public FakeSvnService Svn { get; } = new();
        public FakeHost Host { get; } = new();
        public PathCommands Cmd { get; }
        public int Refreshes;

        public PcFx()
        {
            Directory.CreateDirectory(Path.Combine(Root, "wc", ".svn"));
            File.WriteAllText(Path.Combine(Root, "wc", "a.cs"), "x");
            Cmd = new PathCommands(Svn, Host, () => { Refreshes++; return Task.CompletedTask; }, Path.Combine(Root, "tmp"));
        }

        public string P(string name) => Path.Combine(Root, "wc", name);
        public Task Run(string command, params string[] paths) => Cmd.RunAsync(command, paths, Ct);
        public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task SolutionExplorer_Lock_UnlockCleanup()
    {
        using var f = new PcFx();
        f.Host.TextAnswers.Enqueue("scusate");

        await f.Run("lock", f.P("a.cs"));
        Assert.Equal(("scusate", false), (f.Svn.Locked.Single().Message, f.Svn.Locked.Single().Force));

        await f.Run("unlock", f.P("a.cs"));
        Assert.Single(f.Svn.Unlocked);

        await f.Run("cleanup", f.P("a.cs"));
        Assert.Equal([f.P("a.cs")], f.Svn.CleanedUp);
        Assert.Equal(3, f.Refreshes);
    }

    [Fact]
    public async Task SolutionExplorer_UpdateRev_ApreLaScelta_SuiPercorsiSelezionati()
    {
        using var f = new PcFx();
        var opened = new List<string>();
        var cmd = new PathCommands(f.Svn, f.Host, () => Task.CompletedTask, Path.Combine(f.Root, "tmp"), (t, _) => { opened.AddRange(t); return Task.CompletedTask; });

        await cmd.RunAsync("updaterev", [f.P("a.cs")], Ct);

        Assert.Equal([f.P("a.cs")], opened);
        Assert.Empty(f.Svn.UpdatedToRevision); // l'update parte dalla modale, non dal comando
        Assert.Empty(f.Host.TextPrompts);      // niente piu' dialogo con il campo di testo
    }

    [Fact]
    public async Task SolutionExplorer_Lock_GiaDiUnAltro_ChiedeConfermaPoiForce()
    {
        using var f = new PcFx();
        f.Svn.LockErrorUnlessForced = "svn: E200035: is already locked by user 'bob'";
        f.Host.PromptAnswers.Enqueue(0); // OK al furto

        await f.Run("lock", f.P("a.cs"));

        Assert.True(f.Svn.Locked.Single().Force);
        Assert.Contains("bloccato da un altro", f.Host.Prompts[0].Message);
    }
}
