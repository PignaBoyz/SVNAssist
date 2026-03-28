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
/// Pattern usato: ogni metodo pubblico chiama <see cref="RunSvnAsync"/> che:
/// 1. Avvia <c>svn.exe</c> come processo separato
/// 2. Cattura stdout e stderr in modo asincrono
/// 3. Lancia <see cref="InvalidOperationException"/> se il comando fallisce
/// </remarks>
public class SvnService : ISvnService
{
    private readonly string _svnExePath;

    /// <summary>
    /// Crea una nuova istanza di <see cref="SvnService"/>.
    /// Cerca <c>svn.exe</c> nel PATH e nelle posizioni di installazione note.
    /// </summary>
    /// <param name="svnExePath">
    /// Percorso esplicito di svn.exe (opzionale).
    /// Se null, viene cercato automaticamente nel PATH e nei percorsi standard.
    /// </param>
    /// <exception cref="FileNotFoundException">Se svn.exe non viene trovato.</exception>
    public SvnService(string? svnExePath = null)
    {
        _svnExePath = svnExePath ?? FindSvnExecutable();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct)
    {
        // svn status --xml restituisce un XML con tutti i file modificati.
        // Esempio output:
        //   <status>
        //     <target path=".">
        //       <entry path="file.txt">
        //         <wc-status item="modified" .../>
        //       </entry>
        //     </target>
        //   </status>
        var xml = await RunSvnAsync("status --xml", workingCopyPath, ct);
        var doc = XDocument.Parse(xml);

        var results = new List<SvnFileStatus>();

        foreach (var entry in doc.Descendants("entry"))
        {
            var path = entry.Attribute("path")?.Value ?? string.Empty;
            var itemStatus = entry.Descendants("wc-status")
                                  .FirstOrDefault()
                                  ?.Attribute("item")?.Value ?? "unknown";

            results.Add(new SvnFileStatus(path, ParseStatusKind(itemStatus)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task CommitAsync(IEnumerable<string> paths, string message, CancellationToken ct)
    {
        // Costruisce il comando: svn commit -m "messaggio" "file1" "file2" ...
        // Le virgolette attorno ai path gestiscono spazi nei nomi dei file.
        var pathArgs = string.Join(" ", paths.Select(p => $"\"{p}\""));
        var escapedMessage = message.Replace("\"", "\\\"");
        await RunSvnAsync($"commit -m \"{escapedMessage}\" {pathArgs}", workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(string workingCopyPath, CancellationToken ct)
    {
        await RunSvnAsync("update", workingCopyPath, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct)
    {
        // svn log --xml -l N restituisce le ultime N revisioni in XML.
        // Esempio output:
        //   <log>
        //     <logentry revision="42">
        //       <author>mario</author>
        //       <date>2024-01-15T10:30:00.000000Z</date>
        //       <msg>Fix del bug #123</msg>
        //     </logentry>
        //   </log>
        // Nota: passiamo targetPath come argomento a svn, non come working directory.
        // svn log funziona meglio con il percorso esplicito (supporta sia path locali che URL).
        var xml = await RunSvnAsync($"log --xml -l {maxEntries} \"{targetPath}\"", workingDirectory: null, ct);
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
        // svn diff -c REVISION mostra le modifiche introdotte da quella revisione.
        // L'output è in formato unified diff (testo, non XML).
        return await RunSvnAsync($"diff -c {revision} \"{targetPath}\"", workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task<string> GetWorkingCopyDiffAsync(string workingCopyPath, CancellationToken ct)
    {
        // svn diff senza -c mostra le modifiche locali non committate
        // rispetto alla revisione BASE della working copy.
        return await RunSvnAsync("diff", workingCopyPath, ct);
    }

    /// <summary>
    /// Esegue un comando <c>svn.exe</c> e restituisce l'output stdout.
    /// </summary>
    /// <param name="arguments">Argomenti da passare a svn.exe.</param>
    /// <param name="workingDirectory">Directory di lavoro (null = directory corrente).</param>
    /// <param name="ct">Token di cancellazione.</param>
    /// <returns>L'output standard del comando.</returns>
    /// <exception cref="InvalidOperationException">Se il comando termina con errore (exit code != 0).</exception>
    /// <remarks>
    /// Usiamo <see cref="Process"/> con redirect di stdout/stderr per catturare l'output
    /// in modo asincrono. <c>CreateNoWindow = true</c> evita che appaia una finestra console.
    /// L'alternativa sarebbe usare una libreria SVN nativa (es. SharpSVN), ma questa
    /// richiederebbe .NET Framework e dipendenze native.
    /// </remarks>
    private async Task<string> RunSvnAsync(string arguments, string? workingDirectory, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _svnExePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();

        // Leggiamo stdout e stderr in parallelo per evitare deadlock.
        // Se leggessimo uno alla volta, il buffer dell'altro potrebbe riempirsi
        // e bloccare il processo figlio.
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"svn {arguments.Split(' ')[0]} fallito (exit code {process.ExitCode}): {error}");
        }

        return output;
    }

    /// <summary>
    /// Cerca <c>svn.exe</c> nel PATH di sistema e nelle posizioni di installazione note.
    /// </summary>
    /// <returns>Il percorso completo di svn.exe.</returns>
    /// <exception cref="FileNotFoundException">Se svn.exe non viene trovato.</exception>
    private static string FindSvnExecutable()
    {
        // 1. Prova nel PATH di sistema
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "svn.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. Posizioni di installazione note (TortoiseSVN, CollabNet, SlikSVN)
        string[] knownPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TortoiseSVN", "bin", "svn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TortoiseSVN", "bin", "svn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Subversion", "bin", "svn.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SlikSvn", "bin", "svn.exe"),
        ];

        foreach (var path in knownPaths)
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            "svn.exe non trovato. Installa Subversion e assicurati che sia nel PATH, " +
            "oppure installa TortoiseSVN con l'opzione 'command line client tools'.");
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
