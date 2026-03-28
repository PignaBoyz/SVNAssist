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
