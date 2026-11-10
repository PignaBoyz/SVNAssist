using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>
/// Test di integrazione per <see cref="SvnService"/>.
/// Ogni test crea un repository SVN locale con <c>svnadmin create</c>,
/// fa il checkout di una working copy, e verifica le operazioni.
/// </summary>
/// <remarks>
/// Prerequisito: <c>svn.exe</c> e <c>svnadmin.exe</c> devono essere nel PATH.
/// Questi sono test di integrazione, non unit test — usano il filesystem reale.
/// La classe implementa <see cref="IDisposable"/> per pulire le directory temporanee.
/// </remarks>
public class SvnServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _repoDir;
    private readonly string _wcDir;
    private readonly string _svnExe;
    private readonly string _svnadminExe;
    private readonly SvnService _sut; // SUT = System Under Test

    public SvnServiceTests()
    {
        // Crea una directory temporanea unica per ogni test
        _testDir = Path.Combine(Path.GetTempPath(), "SVNAssist_Test_" + Guid.NewGuid().ToString("N")[..8]);
        _repoDir = Path.Combine(_testDir, "repo");
        _wcDir = Path.Combine(_testDir, "wc");
        Directory.CreateDirectory(_testDir);

        // Trova svn.exe e svnadmin.exe (PATH o posizioni note)
        _svnExe = FindExecutable("svn.exe");
        _svnadminExe = FindExecutable("svnadmin.exe");

        _sut = new SvnService(_svnExe);
    }

    public void Dispose()
    {
        // Pulizia: elimina tutto il contenuto della directory temporanea.
        // SVN crea file read-only nella .svn, quindi dobbiamo rimuovere l'attributo prima.
        try
        {
            ForceDeleteDirectory(_testDir);
        }
        catch
        {
            // Best-effort cleanup — non fallire il test per problemi di pulizia
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetStatusAsync_FileModificato_RestituisceModified()
    {
        // Arrange: crea repo, checkout, aggiungi un file, committa, poi modificalo
        await CreateRepoAndCheckoutAsync();
        var filePath = Path.Combine(_wcDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "contenuto originale");
        await RunAsync(_svnExe, $"add \"{filePath}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m \"primo commit\"", _wcDir);
        await File.WriteAllTextAsync(filePath, "contenuto modificato");

        // Act
        var results = await _sut.GetStatusAsync(_wcDir, CancellationToken.None);

        // Assert
        Assert.Single(results);
        Assert.Equal(SvnStatusKind.Modified, results[0].Status);
        Assert.Contains("test.txt", results[0].Path);
    }

    [Fact]
    public async Task GetStatusAsync_FileAggiunto_RestituisceAdded()
    {
        // Arrange: crea repo, checkout, aggiungi un file (senza commit)
        await CreateRepoAndCheckoutAsync();
        var filePath = Path.Combine(_wcDir, "nuovo.txt");
        await File.WriteAllTextAsync(filePath, "file nuovo");
        await RunAsync(_svnExe, $"add \"{filePath}\"", _wcDir);

        // Act
        var results = await _sut.GetStatusAsync(_wcDir, CancellationToken.None);

        // Assert
        Assert.Single(results);
        Assert.Equal(SvnStatusKind.Added, results[0].Status);
    }

    [Fact]
    public async Task CommitAsync_FileAggiunto_CommitRiuscito()
    {
        // Arrange
        await CreateRepoAndCheckoutAsync();
        var filePath = Path.Combine(_wcDir, "commit_test.txt");
        await File.WriteAllTextAsync(filePath, "da committare");
        await RunAsync(_svnExe, $"add \"{filePath}\"", _wcDir);

        // Act — non deve lanciare eccezioni
        await _sut.CommitAsync([filePath], "test commit", CancellationToken.None);

        // Assert — dopo il commit, lo status deve essere vuoto (nessuna modifica pendente)
        var status = await _sut.GetStatusAsync(_wcDir, CancellationToken.None);
        Assert.Empty(status);
    }

    [Fact]
    public async Task GetLogAsync_DopoCommit_RestituisceRevisione()
    {
        // Arrange: crea repo, checkout, committa un file
        await CreateRepoAndCheckoutAsync();
        var filePath = Path.Combine(_wcDir, "log_test.txt");
        await File.WriteAllTextAsync(filePath, "contenuto per log");
        await RunAsync(_svnExe, $"add \"{filePath}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m \"commit per test log\"", _wcDir);

        // Act — usiamo l'URL del repository perché svn log sulla directory
        // della working copy non restituisce risultati se la root non ha storia diretta.
        var repoUrl = "file:///" + _repoDir.Replace('\\', '/');
        var log = await _sut.GetLogAsync(repoUrl, 10, CancellationToken.None);

        // Assert
        Assert.Single(log);
        Assert.Equal(1, log[0].Revision);
        Assert.Equal("commit per test log", log[0].Message);
    }

    [Fact]
    public async Task UpdateAsync_NonLanciaEccezioni()
    {
        // Arrange
        await CreateRepoAndCheckoutAsync();

        // Act & Assert — update su una working copy pulita non deve fallire
        var exception = await Record.ExceptionAsync(
            () => _sut.UpdateAsync(_wcDir, CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task CommitAsync_RestituisceLaRevisioneCreata()
    {
        await CreateRepoAndCheckoutAsync();
        var filePath = Path.Combine(_wcDir, "rev.txt");
        await File.WriteAllTextAsync(filePath, "x");
        await RunAsync(_svnExe, $"add \"{filePath}\"", _wcDir);

        var revision = await _sut.CommitAsync([filePath], "primo", CancellationToken.None);

        Assert.Equal(1, revision);
    }

    [Fact]
    public async Task UpdateAsync_ConConflitto_RiportaRevisioneEFileInConflitto()
    {
        // Due working copy: la seconda committa, la prima modifica la stessa riga e poi aggiorna.
        await CreateRepoAndCheckoutAsync();
        var file = Path.Combine(_wcDir, "c.txt");
        await File.WriteAllTextAsync(file, "uno\n");
        await RunAsync(_svnExe, $"add \"{file}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m base", _wcDir);

        var wc2 = Path.Combine(_testDir, "wc2");
        await RunAsync(_svnExe, $"checkout \"file:///{_repoDir.Replace('\\', '/')}\" \"{wc2}\"", _testDir);
        await File.WriteAllTextAsync(Path.Combine(wc2, "c.txt"), "loro\n");
        await RunAsync(_svnExe, "commit -m loro", wc2);

        await File.WriteAllTextAsync(file, "mio\n");

        var result = await _sut.UpdateAsync(_wcDir, CancellationToken.None);

        Assert.Equal(2, result.Revision);
        Assert.Single(result.ConflictedPaths);
        Assert.EndsWith("c.txt", result.ConflictedPaths[0]);

        // Con i marcatori ancora nel file, "working" viene rifiutato; "theirs-full" risolve.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ResolveAsync(file, SvnConflictChoice.Working, CancellationToken.None));
        await _sut.ResolveAsync(file, SvnConflictChoice.TheirsFull, CancellationToken.None);
        Assert.Equal("loro", (await File.ReadAllTextAsync(file)).Trim());
        Assert.Empty(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));
    }

    [Fact]
    public async Task GetDiffForAiAsync_SoloFileSelezionati_SenzaContestoEConFileNuovi()
    {
        await CreateRepoAndCheckoutAsync();
        var mod = Path.Combine(_wcDir, "mod.txt");
        var other = Path.Combine(_wcDir, "other.txt");
        var del = Path.Combine(_wcDir, "del.txt");
        await File.WriteAllTextAsync(mod, "a\nb\nc\nd\ne\nf\n");
        await File.WriteAllTextAsync(other, "uno\n");
        await File.WriteAllTextAsync(del, "contenuto da cancellare\n");
        await RunAsync(_svnExe, $"add \"{mod}\" \"{other}\" \"{del}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m init", _wcDir);

        await File.WriteAllTextAsync(mod, "a\nb\nc\nCAMBIATA\ne\nf\n");
        await File.WriteAllTextAsync(other, "non selezionato\n");
        await RunAsync(_svnExe, $"delete \"{del}\"", _wcDir);
        var nuovo = Path.Combine(_wcDir, "nuovo.txt");
        await File.WriteAllTextAsync(nuovo, "prima\nseconda\n");

        var diff = await _sut.GetDiffForAiAsync([mod, del, nuovo], _wcDir, CancellationToken.None);

        Assert.Contains("+CAMBIATA", diff);
        Assert.DoesNotContain("\n e\n", diff.Replace("\r", string.Empty)); // niente righe di contesto
        Assert.DoesNotContain("non selezionato", diff);
        Assert.DoesNotContain("contenuto da cancellare", diff);
        Assert.Contains("del.txt", diff);
        Assert.Contains("--- nuovo.txt\t(nonexistent)", diff);
        Assert.Contains("+seconda", diff);
        Assert.DoesNotContain(_wcDir, diff); // percorsi relativi alla working copy
    }

    // ─── Delete: --force solo dove svn lo richiede ────────────────────

    [Fact]
    public async Task DeleteAsync_FileModificato_SenzaForceSvnRifiuta_ConForceLoCancella()
    {
        await CreateRepoAndCheckoutAsync();
        var file = Path.Combine(_wcDir, "mod.txt");
        await File.WriteAllTextAsync(file, "originale\n");
        await RunAsync(_svnExe, $"add \"{file}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m init", _wcDir);
        await File.WriteAllTextAsync(file, "originale\nmodificato\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteAsync([file], CancellationToken.None));
        Assert.Contains("E195006", ex.Message);
        Assert.True(File.Exists(file));

        await _sut.DeleteAsync([file], CancellationToken.None, force: true);

        Assert.False(File.Exists(file));
        Assert.Equal(SvnStatusKind.Deleted, Assert.Single(await _sut.GetStatusAsync(_wcDir, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task DeleteAsync_FileAggiuntoNonCommittato_RichiedeForce()
    {
        await CreateRepoAndCheckoutAsync();
        var file = Path.Combine(_wcDir, "nuovo.txt");
        await File.WriteAllTextAsync(file, "x\n");
        await RunAsync(_svnExe, $"add \"{file}\"", _wcDir);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.DeleteAsync([file], CancellationToken.None));
        await _sut.DeleteAsync([file], CancellationToken.None, force: true);

        Assert.Empty(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_FileMancante_NonServeForce()
    {
        await CreateRepoAndCheckoutAsync();
        var file = Path.Combine(_wcDir, "gone.txt");
        await File.WriteAllTextAsync(file, "x\n");
        await RunAsync(_svnExe, $"add \"{file}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m init", _wcDir);
        File.Delete(file);

        await _sut.DeleteAsync([file], CancellationToken.None);

        Assert.Equal(SvnStatusKind.Deleted, Assert.Single(await _sut.GetStatusAsync(_wcDir, CancellationToken.None)).Status);
    }

    // ─── Commit come TortoiseSVN: esattamente ciò che è elencato ──────

    [Fact]
    public async Task CommitAsync_CartellaAggiuntaEUnSoloFiglio_CommittaSoloQuelloEIlFratelloRestaAdded()
    {
        await CreateRepoAndCheckoutAsync();
        var folder = Path.Combine(_wcDir, "f");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "b\n");
        await RunAsync(_svnExe, $"add \"{folder}\"", _wcDir);

        var revision = await _sut.CommitAsync(
            [folder, Path.Combine(folder, "a.txt")], "solo a", CancellationToken.None);

        Assert.Equal(1, revision);
        var left = Assert.Single(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));
        Assert.EndsWith("b.txt", left.Path);
        Assert.Equal(SvnStatusKind.Added, left.Status);
    }

    [Fact]
    public async Task CommitAsync_SoloIlFiglioDiUnaCartellaAggiunta_SvnRifiuta_ConLaCartellaRiesce()
    {
        // È il motivo per cui il ViewModel include da sé le cartelle padre nuove.
        await CreateRepoAndCheckoutAsync();
        var folder = Path.Combine(_wcDir, "g");
        var child = Path.Combine(folder, "a.txt");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(child, "a\n");
        await RunAsync(_svnExe, $"add \"{folder}\"", _wcDir);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CommitAsync([child], "m", CancellationToken.None));
        Assert.Contains("E200009", ex.Message);

        Assert.NotNull(await _sut.CommitAsync([folder, child], "m", CancellationToken.None));
        Assert.Empty(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));
    }

    [Fact]
    public async Task CommitAsync_CartellaNuovaAggiuntaPerIntero_IncludeTuttoIlContenuto()
    {
        await CreateRepoAndCheckoutAsync();
        var folder = Path.Combine(_wcDir, "nuova");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "sub", "b.txt"), "b\n");
        await _sut.AddAsync([folder], CancellationToken.None);

        await _sut.CommitAsync([folder], "tutta la cartella", CancellationToken.None, includeAllUnder: [folder]);

        Assert.Empty(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));
    }

    [Fact]
    public async Task CommitAsync_CartellaCancellata_ConDepthEmptyCancellaTuttoIlRamo()
    {
        await CreateRepoAndCheckoutAsync();
        var folder = Path.Combine(_wcDir, "d");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a\n");
        await RunAsync(_svnExe, $"add \"{folder}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m init", _wcDir);
        await _sut.DeleteAsync([folder], CancellationToken.None);

        await _sut.CommitAsync([folder], "via", CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));
    }

    // ─── Riga di comando lunga e nomi particolari ─────────────────────

    [Fact]
    public async Task Add_Commit_Revert_ConCentinaiaDiPercorsiLunghi_UsanoTargetsEFunzionano()
    {
        await CreateRepoAndCheckoutAsync();
        // ~200 file con nomi lunghi (e qualche accento): la riga di comando supererebbe i 24.000 caratteri.
        var files = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var name = new string('n', 100) + (i % 10 == 0 ? $"_perché_{i}" : $"_{i}") + ".txt";
            var path = Path.Combine(_wcDir, name);
            await File.WriteAllTextAsync(path, $"contenuto {i}\n");
            files.Add(path);
        }

        Assert.True(files.Sum(f => f.Length + 3) > SvnService.MaxCommandLineChars);

        await _sut.AddAsync(files, CancellationToken.None);
        Assert.Equal(200, (await _sut.GetStatusAsync(_wcDir, CancellationToken.None)).Count(s => s.Status == SvnStatusKind.Added));

        await _sut.RevertAsync(files, CancellationToken.None);
        Assert.Equal(200, (await _sut.GetStatusAsync(_wcDir, CancellationToken.None)).Count(s => s.Status == SvnStatusKind.Unversioned));

        await _sut.AddAsync(files, CancellationToken.None);
        var revision = await _sut.CommitAsync(files, "tanti file", CancellationToken.None);
        Assert.Equal(1, revision);
        Assert.Empty(await _sut.GetStatusAsync(_wcDir, CancellationToken.None));

        await _sut.DeleteAsync(files, CancellationToken.None);
        Assert.Equal(200, (await _sut.GetStatusAsync(_wcDir, CancellationToken.None)).Count(s => s.Status == SvnStatusKind.Deleted));
    }

    [Fact]
    public async Task GetDiffForAiAsync_ConCentinaiaDiFileLunghi_SpezzaInPiuInvocazioni()
    {
        await CreateRepoAndCheckoutAsync();
        var files = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var path = Path.Combine(_wcDir, new string('m', 100) + $"_{i}.txt");
            await File.WriteAllTextAsync(path, "uno\n");
            files.Add(path);
        }

        await _sut.AddAsync(files, CancellationToken.None);
        await _sut.CommitAsync(files, "init", CancellationToken.None);
        foreach (var path in files)
            await File.WriteAllTextAsync(path, "uno\nDUE\n");

        var diff = await _sut.GetDiffForAiAsync(files, _wcDir, CancellationToken.None);

        Assert.Equal(200, System.Text.RegularExpressions.Regex.Matches(
            diff, @"^\+DUE", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
    }

    [Fact]
    public async Task FileConChiocciolaNelNome_AddCommitDiffRevertDelete_NonSonoLettiComePegRevision()
    {
        await CreateRepoAndCheckoutAsync();
        var file = Path.Combine(_wcDir, "icona@2x.png");
        await File.WriteAllTextAsync(file, "x\n");

        await _sut.AddAsync([file], CancellationToken.None);
        await _sut.CommitAsync([file], "icona", CancellationToken.None);
        await File.WriteAllTextAsync(file, "y\n");
        var diff = await _sut.GetDiffForAiAsync([file], _wcDir, CancellationToken.None);
        await _sut.RevertAsync([file], CancellationToken.None);
        await _sut.DeleteAsync([file], CancellationToken.None);

        Assert.Contains("+y", diff);
        Assert.Equal(SvnStatusKind.Deleted, Assert.Single(await _sut.GetStatusAsync(_wcDir, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task GetDiffForAiAsync_NomeConAccento_IntestazioniLeggibili()
    {
        // svn su Windows scrive le intestazioni nella codepage ANSI e il contenuto in UTF-8.
        await CreateRepoAndCheckoutAsync();
        Directory.CreateDirectory(Path.Combine(_wcDir, "sotto"));
        var file = Path.Combine(_wcDir, "sotto", "città.cs");
        await File.WriteAllTextAsync(file, "ciao\n");
        await RunAsync(_svnExe, $"add \"{Path.Combine(_wcDir, "sotto")}\"", _wcDir);
        await RunAsync(_svnExe, "commit -m init", _wcDir);
        await File.WriteAllTextAsync(file, "ciao\nperché\n");

        var diff = await _sut.GetDiffForAiAsync([file], _wcDir, CancellationToken.None);

        Assert.Contains("Index: sotto/città.cs", diff);
        Assert.Contains("--- sotto/città.cs\t", diff);
        Assert.Contains("+++ sotto/città.cs\t", diff);
        Assert.Contains("+perché", diff);
        Assert.DoesNotContain('�', diff);
    }

    // ─── Helper methods ───────────────────────────────────────────────

    /// <summary>
    /// Crea un repository SVN locale e fa il checkout di una working copy.
    /// Usato come setup per ogni test.
    /// </summary>
    private async Task CreateRepoAndCheckoutAsync()
    {
        // svnadmin create crea un repository SVN vuoto sul filesystem locale
        await RunAsync(_svnadminExe, $"create \"{_repoDir}\"", _testDir);

        // Checkout dalla URL file:/// (protocollo locale, nessun server necessario)
        var repoUrl = "file:///" + _repoDir.Replace('\\', '/');
        await RunAsync(_svnExe, $"checkout \"{repoUrl}\" \"{_wcDir}\"", _testDir);
    }

    /// <summary>
    /// Esegue un comando esterno e attende il completamento.
    /// </summary>
    private static async Task RunAsync(string fileName, string arguments, string workingDirectory)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {arguments} fallito: {error}");
    }

    /// <summary>
    /// Elimina una directory ricorsivamente, anche se contiene file read-only
    /// (SVN marca alcuni file nella cartella .svn come read-only).
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Cerca un eseguibile nel PATH di sistema e nelle posizioni di installazione note.
    /// </summary>
    private static string FindExecutable(string exeName)
    {
        // 1. Cerca nel PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. Posizioni note di TortoiseSVN
        string[] knownDirs =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseSVN", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TortoiseSVN", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Subversion", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SlikSvn", "bin"),
        ];

        foreach (var dir in knownDirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"{exeName} non trovato. Installa SVN con i command-line tools.");
    }
}
