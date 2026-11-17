using System.Diagnostics;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Bug 4 (0.4.0): merge di UNA cartella dalla cartella equivalente di un altro ramo, con verdetto di compatibilita'.</summary>
public class FolderMergeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("svn: E160013: File not found: revision 8, path '/branches/f/nope'", "non esiste")]
    [InlineData("svn: E195016: 'x@8' must be ancestrally related to 'y@2'", "nessun antenato comune")]
    [InlineData("svn: E195012: Unable to find repository location", "nessun antenato comune")]
    [InlineData("svn: E999999: altro problema\nseconda riga", "svn risponde: svn: E999999: altro problema")]
    public void ExplainSubtreeMergeError_MessaggiSvnTradotti(string svn, string expected)
        => Assert.Contains(expected, ChangesStore.ExplainSubtreeMergeError(svn, "modX", "branches/f"));

    [Fact]
    public async Task SvnVero_Sottoalbero_Compatibile_Inesistente_NonCorrelato_EAnnullaLimitato()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_FoldMg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string Run(string exe, string args, string cwd)
            {
                using var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var o = p.StandardOutput.ReadToEnd();
                var e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, $"{args}: {e}");
                return o;
            }

            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            Run(svnExe, $"mkdir -m i \"{url}/trunk\" \"{url}/branches\"", root);
            var wc = Path.Combine(root, "wc");
            Run(svnExe, $"checkout \"{url}/trunk\" \"{wc}\"", root);
            Directory.CreateDirectory(Path.Combine(wc, "modX"));
            Directory.CreateDirectory(Path.Combine(wc, "lib"));
            File.WriteAllText(Path.Combine(wc, "modX", "a.txt"), "a\n");
            File.WriteAllText(Path.Combine(wc, "lib", "l.txt"), "l\n");
            Run(svnExe, "add modX lib", wc);
            Run(svnExe, "commit -m base", wc);
            Run(svnExe, $"copy \"{url}/trunk\" \"{url}/branches/f\" -m br", root);
            var bwc = Path.Combine(root, "bwc");
            Run(svnExe, $"checkout \"{url}/branches/f\" \"{bwc}\"", root);
            File.AppendAllText(Path.Combine(bwc, "modX", "a.txt"), "b\n");
            File.AppendAllText(Path.Combine(bwc, "lib", "l.txt"), "m\n");
            Run(svnExe, "commit -m feat", bwc);
            Run(svnExe, $"mkdir -m u \"{url}/branches/g\"", root);
            Run(svnExe, $"mkdir -m u \"{url}/branches/g/modX\"", root);
            Run(svnExe, "update", wc);

            var svn = new SvnService(svnExe);
            var modX = Path.Combine(wc, "modX");

            // compatibile: eligible e anteprima solo sul sottoalbero
            Assert.Single(await svn.GetEligibleRevisionsAsync(modX, url + "/branches/f/modX", Ct));
            var dry = await svn.MergeAsync(modX, url + "/branches/f/modX", null, dryRun: true, Ct);
            Assert.Equal(["a.txt"], dry.Items.Select(i => i.Path));

            // sottopercorso inesistente / senza antenato comune: errori riconosciuti e spiegati
            var ex1 = await Assert.ThrowsAnyAsync<Exception>(() => svn.GetEligibleRevisionsAsync(modX, url + "/branches/f/nope", Ct));
            Assert.Contains("non esiste", ChangesStore.ExplainSubtreeMergeError(ex1.Message, "nope", "branches/f"));
            var ex2 = await Assert.ThrowsAnyAsync<Exception>(() => svn.MergeAsync(modX, url + "/branches/g/modX", null, dryRun: true, Ct));
            Assert.Contains("antenato comune", ChangesStore.ExplainSubtreeMergeError(ex2.Message, "modX", "branches/g"));

            // il merge vero tocca solo modX; annullare modX lascia lib com'era (qui intatta) e la working copy pulita
            await svn.MergeAsync(modX, url + "/branches/f/modX", null, dryRun: false, Ct);
            Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(modX, "a.txt")).Replace("\r\n", "\n"));
            Assert.Equal("l\n", File.ReadAllText(Path.Combine(wc, "lib", "l.txt")).Replace("\r\n", "\n"));
            await svn.RevertAsync([modX], Ct, recursive: true);
            Assert.DoesNotContain(await svn.GetStatusAsync(wc, Ct), s => s.Status != SvnStatusKind.Unversioned);
        }
        finally
        {
            try { foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(root, true); } catch { }
        }
    }
}
