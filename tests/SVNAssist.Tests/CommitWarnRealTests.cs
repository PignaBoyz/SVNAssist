using System.Diagnostics;
using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Commit All con non versionati (cartelle, .vs, maiuscole) su una working copy vera: la domanda deve comparire e lo stato deve essere serializzabile.</summary>
public class CommitWarnRealTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string Run(string exe, string args, string cwd)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{Path.GetFileName(exe)} {args}: {e}");
        return o;
    }

    [Fact]
    public async Task SvnVero_CommitAll_ConNonVersionati_MostraDomandaEStatoSerializzabile()
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_CwReal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            var wc = Path.Combine(root, "wc");
            Run(svnExe, $"checkout \"{url}\" \"{wc}\"", root);
            Directory.CreateDirectory(Path.Combine(wc, "src", "Shop"));
            foreach (var n in new[] { "Catalog.cs", "Invoice.cs", "Order.cs" })
                File.WriteAllText(Path.Combine(wc, "src", "Shop", n), "x\n");
            Run(svnExe, "add src", wc);
            Run(svnExe, "commit -m base", wc);
            var shop = Path.Combine(wc, "src", "Shop");
            File.WriteAllText(Path.Combine(shop, "Catalog.cs"), "y\n");
            File.WriteAllText(Path.Combine(shop, "Order.cs"), "y\n");
            File.WriteAllText(Path.Combine(shop, "Foo.cs"), "n\n");
            File.WriteAllText(Path.Combine(shop, "Note.txt"), "n\n");
            File.WriteAllText(Path.Combine(shop, "Payment.cs"), "n\n");
            Run(svnExe, "add src/Shop/Foo.cs src/Shop/Note.txt src/Shop/Payment.cs", wc);
            Run(svnExe, "rm src/Shop/Invoice.cs", wc);
            // non versionati: cartelle con file dentro, .vs, e un file che differisce da Note.txt solo per le maiuscole
            Directory.CreateDirectory(Path.Combine(shop, "bin", "Debug"));
            File.WriteAllText(Path.Combine(shop, "bin", "Debug", "a.dll"), "b");
            Directory.CreateDirectory(Path.Combine(shop, "obj"));
            File.WriteAllText(Path.Combine(shop, "obj", "b.cache"), "b");
            Directory.CreateDirectory(Path.Combine(wc, ".vs", "x"));
            File.WriteAllText(Path.Combine(wc, ".vs", "x", "s.suo"), "b");
            File.WriteAllText(Path.Combine(shop, "note.txt"), "n\n");

            var settings = new StubSettings();
            using var store = new ChangesStore(settings, new SvnService(svnExe), new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())),
                recentTargetsFile: Path.Combine(root, "recent.json"))
            { WorkingCopyPath = wc, TempRoot = Path.Combine(root, "tmp") };
            await store.RefreshAsync(Ct);
            store.CommitMessage.SetTextQuietly("messaggio di prova");
            await store.DispatchAsync("commit", JsonSerializer.SerializeToElement(new { }), Ct);

            var snap = (ChangesState)store.GetSnapshot();
            Assert.NotNull(snap.CommitWarn);
            var json = UiJson.Serialize(snap);
            
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("commitWarn").GetProperty("count").GetInt32() == 2); // .vs non conta
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* file .svn in sola lettura */ }
        }
    }

    /// <summary>Il caso reale: un collega committa un ALTRO file poco prima; il nostro commit passerebbe senza dire nulla e la build perderebbe il suo lavoro.</summary>
    [Theory]
    [InlineData(false)] // "Annulla": niente commit, pulsante SVN Update
    [InlineData(true)]  // "Committa comunque": commit fatto, avviso e indicatore arancio
    public async Task SvnVero_CommitConWorkingCopyIndietro_AvvisaPrimaEDopo(bool committaComunque)
    {
        var svnExe = SvnLocator.FindSvn() ?? throw new InvalidOperationException("svn.exe non trovato");
        var admin = SvnLocator.FindSvnAdmin() ?? throw new InvalidOperationException("svnadmin.exe non trovato");
        var root = Path.Combine(Path.GetTempPath(), "SVNAssist_Behind_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "repo");
            var url = "file:///" + repo.Replace('\\', '/');
            Run(admin, $"create \"{repo}\"", root);
            var wc = Path.Combine(root, "wc");
            var collega = Path.Combine(root, "collega");
            Run(svnExe, $"checkout \"{url}\" \"{wc}\"", root);
            File.WriteAllText(Path.Combine(wc, "Mio.cs"), "x\n");
            File.WriteAllText(Path.Combine(wc, "Suo.cs"), "x\n");
            Run(svnExe, "add Mio.cs Suo.cs", wc);
            Run(svnExe, "commit -m base", wc);
            Run(svnExe, "update", wc);
            Run(svnExe, $"checkout \"{url}\" \"{collega}\"", root);
            File.WriteAllText(Path.Combine(collega, "Suo.cs"), "lavoro del collega\n");
            Run(svnExe, "commit -m collega", collega);
            File.WriteAllText(Path.Combine(wc, "Mio.cs"), "mio\n");

            var settings = new StubSettings();
            var dialogs = new FakeDialogs { AskResult = committaComunque };
            using var store = new ChangesStore(settings, new SvnService(svnExe), new FakeAiFactory(), new NoopAiPrompt(), dialogs, new FakeHost(),
                new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())),
                recentTargetsFile: Path.Combine(root, "recent.json"))
            { WorkingCopyPath = wc, TempRoot = Path.Combine(root, "tmp") };
            await store.RefreshAsync(Ct, reloadBranch: true);
            store.CommitMessage.SetTextQuietly("mio commit");
            await store.DispatchAsync("commit", JsonSerializer.SerializeToElement(new { }), Ct);

            Assert.Single(dialogs.Asked);
            var snap = (ChangesState)store.GetSnapshot();
            Assert.Equal("update", snap.Status.Action?.Cmd);
            var log = Run(svnExe, $"log -q \"{url}\"", root);
            Assert.Equal(committaComunque, log.Contains("r3 "));
            if (committaComunque)
                Assert.Contains("SVN Update", snap.Status.Msg); // l'indicatore arancio arriva dal controllo HEAD rilanciato (Nudge), qui non attivo
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* file .svn in sola lettura */ }
        }
    }
}
