using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using SVNAssist.Models;

namespace SVNAssist.Services;

/// <summary>
/// Implementazione di <see cref="ISvnService"/> che usa <c>svn.exe</c> via command-line.
/// </summary>
/// <remarks>
/// Perché <c>svn.exe</c> invece di SharpSVN?
/// - SharpSVN è una libreria nativa che funziona solo su .NET Framework
/// - <c>svn.exe</c> funziona su qualsiasi piattaforma dove SVN è installato
/// - L'output XML (<c>--xml</c>) è stabile e ben documentato
/// - Non ci sono dipendenze native da gestire
///
/// L'esecuzione del processo è delegata a <see cref="IProcessRunner"/>: così la costruzione
/// degli argomenti è unit-testabile con un runner finto (senza SVN installato), mentre lo
/// spawn reale vive solo in <see cref="ProcessRunner"/>. Gli argomenti sono passati come
/// lista (non come stringa concatenata): l'escaping è gestito dal runtime.
/// </remarks>
public class SvnService : ISvnService
{
    private readonly string _svnExePath;
    private readonly IProcessRunner _processRunner;

    /// <summary>
    /// Crea una nuova istanza di <see cref="SvnService"/>.
    /// </summary>
    /// <param name="svnExePath">
    /// Percorso esplicito di svn.exe (opzionale).
    /// Se null, viene cercato automaticamente da <see cref="SvnLocator"/>.
    /// </param>
    /// <param name="processRunner">
    /// Runner per l'esecuzione dei processi (opzionale). Se null, usa <see cref="ProcessRunner"/>.
    /// Nei test si inietta un runner finto che cattura gli argomenti.
    /// </param>
    /// <exception cref="FileNotFoundException">Se svn.exe non viene trovato.</exception>
    public SvnService(string? svnExePath = null, IProcessRunner? processRunner = null)
    {
        _svnExePath = svnExePath ?? SvnLocator.FindSvn() ?? throw new FileNotFoundException(
            "svn.exe non trovato. Installa Subversion e assicurati che sia nel PATH, " +
            "oppure installa TortoiseSVN con l'opzione 'command line client tools'.");
        _processRunner = processRunner ?? new ProcessRunner();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct)
    {
        // svn status --xml restituisce un XML con tutti i file modificati.
        var xml = await RunSvnAsync(["status", "--xml"], workingCopyPath, ct);
        var doc = XDocument.Parse(xml);

        var results = new List<SvnFileStatus>();

        foreach (var entry in doc.Descendants("entry"))
        {
            var path = entry.Attribute("path")?.Value ?? string.Empty;
            var wcStatus = entry.Descendants("wc-status").FirstOrDefault();
            var itemStatus = wcStatus?.Attribute("item")?.Value ?? "unknown";
            var propsStatus = wcStatus?.Attribute("props")?.Value ?? "none";

            // Quando cambia solo una proprietà (es. svn:ignore), svn status --xml
            // restituisce item="normal" e props="modified": va trattato come modifica.
            if (itemStatus == "normal" && propsStatus == "modified")
                itemStatus = "modified";

            results.Add(new SvnFileStatus(path, ParseStatusKind(itemStatus)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task CommitAsync(IEnumerable<string> paths, string message, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        // svn commit -m <messaggio> <file...>
        // Il messaggio e i path sono argomenti singoli: niente escaping manuale.
        var args = new List<string> { "commit", "-m", message };
        args.AddRange(pathList);
        await RunSvnAsync(args, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task AddAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        var args = new List<string> { "add", "--force" };
        args.AddRange(pathList);
        await RunSvnAsync(args, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        var args = new List<string> { "delete" };
        args.AddRange(pathList);
        await RunSvnAsync(args, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task RevertAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        var args = new List<string> { "revert", "--depth", "infinity" };
        args.AddRange(pathList);
        await RunSvnAsync(args, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task ResolveAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Il percorso non può essere vuoto.", nameof(path));

        await RunSvnAsync(["resolve", "--accept", "working", path], workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task AddToIgnoreListAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Il percorso non può essere vuoto.", nameof(path));

        var targetName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(targetName))
            throw new InvalidOperationException("Impossibile determinare il nome dell'elemento da ignorare.");

        var parentDirectory = Directory.Exists(path)
            ? Directory.GetParent(path)?.FullName
            : Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new InvalidOperationException("Impossibile determinare la directory padre per svn:ignore.");

        string existingIgnore;
        try
        {
            existingIgnore = (await RunSvnAsync(["propget", "svn:ignore", parentDirectory], workingDirectory: null, ct)).TrimEnd();
        }
        catch
        {
            existingIgnore = string.Empty;
        }

        var entries = existingIgnore
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (entries.Any(v => string.Equals(v, targetName, StringComparison.OrdinalIgnoreCase)))
            return;

        entries.Add(targetName);

        // Il valore multilinea è passato come singolo argomento: ArgumentList lo gestisce
        // correttamente, senza bisogno di escaping manuale delle virgolette/newline.
        var newIgnoreValue = string.Join("\n", entries);
        await RunSvnAsync(["propset", "svn:ignore", newIgnoreValue, parentDirectory], workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(string workingCopyPath, CancellationToken ct)
    {
        await RunSvnAsync(["update"], workingCopyPath, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct)
    {
        // svn log --xml -l N <target> restituisce le ultime N revisioni in XML.
        // Passiamo targetPath come argomento (supporta sia path locali che URL).
        var xml = await RunSvnAsync(
            ["log", "--xml", "-l", maxEntries.ToString(), targetPath], workingDirectory: null, ct);
        var doc = XDocument.Parse(xml);

        var results = new List<SvnLogEntry>();

        foreach (var entry in doc.Descendants("logentry"))
        {
            var revision = long.Parse(entry.Attribute("revision")?.Value ?? "0");
            var author = entry.Element("author")?.Value ?? "(sconosciuto)";
            var date = DateTimeOffset.Parse(entry.Element("date")?.Value ?? DateTimeOffset.MinValue.ToString("O"));
            var message = entry.Element("msg")?.Value ?? string.Empty;

            results.Add(new SvnLogEntry(revision, author, date, message));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct)
    {
        // svn diff -c REVISION mostra le modifiche introdotte da quella revisione (unified diff).
        return await RunSvnAsync(["diff", "-c", revision.ToString(), targetPath], workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task<string> GetWorkingCopyDiffAsync(string workingCopyPath, CancellationToken ct)
    {
        // svn diff senza -c mostra le modifiche locali non committate rispetto alla revisione BASE.
        return await RunSvnAsync(["diff"], workingCopyPath, ct);
    }

    /// <inheritdoc />
    public async Task<string> GetFileDiffAsync(string filePath, CancellationToken ct)
    {
        // svn diff per un singolo file: modifiche locali non committate rispetto a BASE.
        return await RunSvnAsync(["diff", filePath], workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task<bool> OpenNativeDiffAsync(string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Il percorso file non può essere vuoto.", nameof(filePath));

        ct.ThrowIfCancellationRequested();

        var tortoiseProcPath = SvnLocator.FindTortoiseProc();
        if (string.IsNullOrWhiteSpace(tortoiseProcPath))
            return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = tortoiseProcPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/command:diff");
        startInfo.ArgumentList.Add($"/path:{filePath}");

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var process = Process.Start(startInfo);
            return process is not null;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<string> GetBranchNameAsync(string workingCopyPath, CancellationToken ct)
    {
        // svn info --show-item relative-url restituisce il path relativo alla root del repository.
        // Esempio output: ^/trunk  oppure  ^/branches/feature-test
        try
        {
            var relativeUrl = await RunSvnAsync(
                ["info", "--show-item", "relative-url"], workingCopyPath, ct);

            var url = relativeUrl.Trim().TrimStart('^', '/');
            return string.IsNullOrWhiteSpace(url) ? "unknown" : url;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Cerca la root della working copy SVN navigando verso l'alto nel filesystem.
    /// Cerca la cartella <c>.svn</c> nella directory corrente e in quelle superiori.
    /// </summary>
    /// <param name="startPath">Percorso da cui iniziare la ricerca.</param>
    /// <param name="maxLevels">Numero massimo di livelli da risalire (default 10).</param>
    /// <returns>Il percorso della root SVN, oppure <c>null</c> se non trovata.</returns>
    public static string? FindSvnRoot(string startPath, int maxLevels = 10)
    {
        var current = new DirectoryInfo(startPath);

        for (int i = 0; i < maxLevels && current != null; i++)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".svn")))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Esegue un comando <c>svn.exe</c> tramite <see cref="IProcessRunner"/> e restituisce stdout.
    /// </summary>
    /// <param name="arguments">Argomenti del comando (uno per elemento).</param>
    /// <param name="workingDirectory">Directory di lavoro (null = corrente).</param>
    /// <param name="ct">Token di cancellazione.</param>
    /// <returns>L'output standard del comando.</returns>
    /// <exception cref="InvalidOperationException">Se il comando termina con errore (exit code != 0).</exception>
    private async Task<string> RunSvnAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
    {
        var result = await _processRunner.RunAsync(_svnExePath, arguments, workingDirectory, ct);

        if (result.ExitCode != 0)
        {
            var subcommand = arguments.Count > 0 ? arguments[0] : "?";
            throw new InvalidOperationException(
                $"svn {subcommand} fallito (exit code {result.ExitCode}): {result.StandardError}");
        }

        return result.StandardOutput;
    }

    /// <summary>
    /// Converte la stringa dell'attributo <c>item</c> dell'XML di svn status
    /// nel corrispondente valore <see cref="SvnStatusKind"/>.
    /// </summary>
    private static SvnStatusKind ParseStatusKind(string itemStatus) => itemStatus switch
    {
        "normal" => SvnStatusKind.Normal,
        "added" => SvnStatusKind.Added,
        "modified" => SvnStatusKind.Modified,
        "deleted" => SvnStatusKind.Deleted,
        "conflicted" => SvnStatusKind.Conflicted,
        "unversioned" => SvnStatusKind.Unversioned,
        "missing" => SvnStatusKind.Missing,
        "replaced" => SvnStatusKind.Replaced,
        _ => SvnStatusKind.Unknown,
    };
}
