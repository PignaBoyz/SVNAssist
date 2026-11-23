using System.Diagnostics;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>D16: limiti AI per provider. F1: totale della barra di avanzamento da <c>svn diff --summarize</c>.</summary>
public class ProviderLimitsAndTotalTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void OgniProviderDichiaraILimiti_AlzaSoloQuelliConContestoAmpio()
    {
        var http = new HttpClient();
        Assert.Same(AiLimits.Anthropic, new AnthropicAiService("k", "m").Limits);
        Assert.Same(AiLimits.ClaudeCode, new ClaudeCodeAiService(new ProcessRunner(), "claude.exe", "m").Limits);
        Assert.Same(AiLimits.Copilot, new CopilotAiService(http, null, "").Limits);
        Assert.Same(AiLimits.GitHubModels, new AiService(http, "https://models.github.ai/inference/chat/completions", "m", "k").Limits);
        Assert.Same(AiLimits.Gemini, new AiService(http, AiServiceFactory.GeminiEndpoint, "gemini-2.5-flash", "k").Limits);
        Assert.Same(AiLimits.OpenAiCompatible, new AiService(http, "http://localhost:11434/v1/chat/completions", "m", "").Limits);
        Assert.True(AiLimits.Anthropic.DiffMaxTotalChars > AiLimits.GitHubModels.DiffMaxTotalChars);
        Assert.True(AiLimits.Gemini.DiffMaxTotalChars > AiLimits.GitHubModels.DiffMaxTotalChars);
        Assert.True(AiLimits.Copilot.MaxUserChars < AiLimits.Anthropic.MaxUserChars);
    }

    [Fact]
    public void DiffLimiter_UsaILimitiDelProvider()
    {
        var diff = "Index: a.cs\n===\n--- a.cs\n+++ a.cs\n@@ -1 +1 @@\n" + string.Join('\n', Enumerable.Repeat("+" + new string('x', 100), 300)) + "\n"; // ~30k
        Assert.True(DiffLimiter.Limit(diff, AiLimits.GitHubModels).WasTruncated);
        Assert.False(DiffLimiter.Limit(diff, AiLimits.Anthropic).WasTruncated);
    }

    [Fact]
    public void Progress_TotaleSoloSePositivo_EsceNelloSnapshot()
    {
        var p = new OperationProgress();
        p.Begin("update", "t", "f");
        Assert.Null(p.GetSnapshot()!.Total);
        p.SetTotal(0);
        Assert.Null(p.GetSnapshot()!.Total);
        p.SetTotal(7);
        Assert.Equal(7, p.GetSnapshot()!.Total);
        p.Begin("switch", "t", "f"); // una nuova operazione riparte senza totale
        Assert.Null(p.GetSnapshot()!.Total);
    }

    private static string Run(string exe, string args, string cwd)
    {
        using var pr = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var o = pr.StandardOutput.ReadToEnd();
        var e = pr.StandardError.ReadToEnd();
        pr.WaitForExit();
        Assert.True(pr.ExitCode == 0, $"{Path.GetFileName(exe)} {args}: {e}");
        return o;
    }

    [Fact]
    public async Task SvnVero_CountChanges_ContaGliElementiDiUpdateESwitch()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_Cnt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            Run(svnExe, $"mkdir -m base \"{url}/trunk\" \"{url}/branches\"", root);
            var wc = Path.Combine(root, "wc");
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc}\"", root);
            File.WriteAllText(Path.Combine(wc, "a.txt"), "a\n");
            Run(svnExe, "add a.txt", wc);
            Run(svnExe, "commit -m r2", wc);
            Run(svnExe, "update", wc);
            Run(svnExe, $"copy -m br \"{url}/trunk\" \"{url}/branches/b\"", root);
            var other = Path.Combine(root, "other");
            Run(svnExe, $"checkout \"{url}/branches/b\" \"{other}\"", root);
            File.WriteAllText(Path.Combine(other, "b1.txt"), "1\n");
            File.WriteAllText(Path.Combine(other, "b2.txt"), "2\n");
            Run(svnExe, "add b1.txt b2.txt", other);
            Run(svnExe, "commit -m r4", other);

            var sut = new SvnService(svnExe, new ProcessRunner());
            Assert.Equal(2, await sut.CountChangesAsync(wc, $"{url}/branches/b", null, Ct)); // switch trunk -> b: b1 e b2 in piu'
            Assert.Equal(0, await sut.CountChangesAsync(wc, null, null, Ct));                 // update senza novita'
            Assert.Null(await sut.CountChangesAsync(Path.Combine(root, "inesistente"), null, null, Ct)); // errore: resta indeterminata
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* file .svn di sola lettura: best effort */ }
        }
    }
}
