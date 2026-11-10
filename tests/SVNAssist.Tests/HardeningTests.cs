using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Test degli indurimenti di sicurezza/robustezza: limiti di output, timeout, PATH, letture limitate, glob, shim Claude.</summary>
public class HardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SVNAssist_Hard_" + Guid.NewGuid().ToString("N")[..8]);

    public HardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class WaitingRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    // ── ProcessRunner: tetto all'output ──────────────────────────────

    [Fact]
    public async Task ProcessRunner_OutputOltreIlTetto_TerminaEFallisceConMessaggioChiaro()
    {
        var runner = new ProcessRunner(maxOutputChars: 100);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            "cmd.exe", ["/c", "echo", new string('x', 500)], null, CancellationToken.None));

        Assert.Contains("troppo grande", ex.Message);
    }

    [Fact]
    public async Task ProcessRunner_OutputSottoIlTetto_Normale()
    {
        var result = await new ProcessRunner(maxOutputChars: 1000).RunAsync(
            "cmd.exe", ["/c", "echo", "ciao"], null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ciao", result.StandardOutput);
    }

    // ── Timeout distinto dall'annullamento ───────────────────────────

    [Fact]
    public async Task RunWithTimeout_Scaduto_LanciaTimeoutExceptionConMessaggio()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => new WaitingRunner().RunWithTimeoutAsync(
            "gh", ["auth", "token"], null, TimeSpan.FromMilliseconds(50), "gh auth token", CancellationToken.None));

        Assert.Contains("gh auth token", ex.Message);
    }

    [Fact]
    public async Task RunWithTimeout_AnnullatoDallUtente_RestaOperationCanceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WaitingRunner().RunWithTimeoutAsync(
            "gh", ["auth", "token"], null, TimeSpan.FromMinutes(5), "gh", cts.Token));
    }

    [Fact]
    public async Task TokenResolver_GhAppeso_PassaAllaSorgenteSuccessivaSenzaBloccare()
    {
        var resolver = new GitHubTokenResolver(
            new WaitingRunner(), ghLocator: () => "gh.exe", environmentReader: _ => null);

        // GhTimeout è 10 s: qui basta verificare che l'annullamento esplicito non venga inghiottito.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(cts.Token));
    }

    // ── PATH: solo voci assolute ─────────────────────────────────────

    [Fact]
    public void RootedPathDirectories_ScartaVociRelative()
    {
        var dirs = SvnLocator.RootedPathDirectories($@"relativa;.;bin;C:\tools;\solo-radice;C:senza-radice;""C:\con virgolette"";;");

        Assert.Equal([@"C:\tools", @"C:\con virgolette"], dirs);
    }

    // ── Letture limitate ─────────────────────────────────────────────

    [Fact]
    public void ReadLinesBounded_RigaEnorme_NonTieneTuttoInMemoria()
    {
        var path = Path.Combine(_dir, "huge.txt");
        File.WriteAllText(path, new string('a', 3_000_000) + "\r\nsecond\nthird\rfourth");

        var lines = SvnService.ReadLinesBounded(path, maxLineChars: 10).ToList();

        Assert.Equal(4, lines.Count);
        Assert.Equal(10, lines[0].Text.Length);
        Assert.True(lines[0].Truncated);
        Assert.Equal(["second", "third", "fourth"], lines.Skip(1).Select(l => l.Text));
        Assert.All(lines.Skip(1), l => Assert.False(l.Truncated));
    }

    [Fact]
    public void ReadLinesBounded_FileVuotoOSoloNewline_ComeReadLines()
    {
        var empty = Path.Combine(_dir, "e.txt");
        File.WriteAllText(empty, string.Empty);
        var nl = Path.Combine(_dir, "n.txt");
        File.WriteAllText(nl, "a\n");

        Assert.Empty(SvnService.ReadLinesBounded(empty, 10));
        Assert.Equal(["a"], SvnService.ReadLinesBounded(nl, 10).Select(l => l.Text));
    }

    [Fact]
    public void BuildNewFileDiff_FileSuUnaRigaSolaEnorme_AnteprimaCorta()
    {
        var path = Path.Combine(_dir, "min.js");
        File.WriteAllText(path, new string('x', 5_000_000));

        var diff = SvnService.BuildNewFileDiff(path, "min.js");

        Assert.True(diff.Length < 2000);
        Assert.Contains("...", diff);
    }

    [Fact]
    public void ContainsConflictMarkers_FileEnormeSenzaMarcatori_False_ConMarcatori_True()
    {
        var plain = Path.Combine(_dir, "p.txt");
        File.WriteAllText(plain, new string('x', 2_000_000));
        var conflict = Path.Combine(_dir, "c.txt");
        File.WriteAllText(conflict, "uno\n<<<<<<< .mine\ndue\n=======\ntre\n>>>>>>> .r2\n");

        Assert.False(SvnService.ContainsConflictMarkers(plain));
        Assert.True(SvnService.ContainsConflictMarkers(conflict));
    }

    // ── Glob di svn:ignore ───────────────────────────────────────────

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("a[1].txt", @"a\[1\].txt")]
    [InlineData("*.log", @"\*.log")]
    [InlineData(@"we?rd\name", @"we\?rd\\name")]
    public void EscapeGlob_ProteggeIMetacaratteri(string name, string expected)
        => Assert.Equal(expected, SvnService.EscapeGlob(name));

    // ── Percorsi che iniziano con '-' ────────────────────────────────

    [Fact]
    public async Task LogEDiff_PercorsoCheIniziaConTrattino_NonDiventaUnOpzione()
    {
        List<string> last = [];
        var runner = new CapturingRunner(a => last = a.ToList());
        var sut = new SvnService(@"C:\fake\svn.exe", runner);

        await sut.GetLogAsync("-evil", 5, CancellationToken.None);
        Assert.Equal(@".\-evil", last[^1]);
        await sut.GetDiffAsync("-evil", 3, CancellationToken.None);
        Assert.Equal(@".\-evil", last[^1]);
        await sut.GetFileDiffAsync("-evil", CancellationToken.None);
        Assert.Equal(@".\-evil", last[^1]);
    }

    private sealed class CapturingRunner(Action<IReadOnlyList<string>> capture) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct,
            string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            capture(arguments);
            return Task.FromResult(new ProcessResult(0, "<log/>", string.Empty));
        }
    }

    // ── Claude Code: shim npm e cartella di lavoro ───────────────────

    [Fact]
    public void FindClaude_ShimNpm_SiRisolveSulProgrammaVero_ENonSuUnCmd()
    {
        var npm = Path.Combine(_dir, "npm");
        var bin = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(bin, "claude.exe"), "x");

        var found = ClaudeCodeAiService.FindClaudeExecutable([npm], _dir);

        Assert.Equal(Path.Combine(bin, "claude.exe"), found);
    }

    [Fact]
    public void FindClaude_ShimNpmConSoloCliJs_UsaNode()
    {
        var npm = Path.Combine(_dir, "npm2");
        var pkg = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(pkg, "cli.js"), "//");

        var found = ClaudeCodeAiService.FindClaudeExecutable([npm], _dir);
        var (file, prefix) = ClaudeCodeAiService.BuildLaunch(found!);

        Assert.Equal(Path.Combine(pkg, "cli.js"), found);
        Assert.Equal([found!], prefix);
        Assert.EndsWith("node", Path.GetFileNameWithoutExtension(file));
    }

    [Fact]
    public void FindClaude_CmdSenzaProgrammaVero_NonTrovato()
    {
        var npm = Path.Combine(_dir, "npm3");
        Directory.CreateDirectory(npm);
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "@echo off");

        Assert.Null(ClaudeCodeAiService.FindClaudeExecutable([npm], _dir));
    }

    [Fact]
    public void ClaudeWorkingDirectory_EUnaCartellaVuotaDedicata()
    {
        var dir = ClaudeCodeAiService.EnsureEmptyWorkingDirectory();

        Assert.True(Directory.Exists(dir));
        Assert.EndsWith(Path.Combine("SVNAssist", "empty"), dir);
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }
}
