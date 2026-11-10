using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>Timeout predefinito per ogni invocazione di svn.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    // L'output di svn è localizzato (es. "Aggiornato alla revisione 2."): per parsare l'output
    // testuale (update, commit) lo si forza in inglese. Gli XML non dipendono dalla lingua.
    private static readonly IReadOnlyDictionary<string, string> SvnProcessEnvironment =
        new Dictionary<string, string> { ["LC_ALL"] = "C", ["LANG"] = "C" };

    // Anteprima dei file nuovi non versionati nel diff per l'AI.
    private const int NewFilePreviewLines = 60;
    private const int NewFileMaxLineChars = 300;
    private const int BinarySniffBytes = 8192;
    private const long NewFileCountLinesMaxBytes = 1024 * 1024;

    private readonly string _svnExePath;
    private readonly IProcessRunner _processRunner;
    private readonly TimeSpan _timeout;

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
    /// <param name="timeout">
    /// Tempo massimo per singola invocazione di svn (default <see cref="DefaultTimeout"/>).
    /// Scaduto, il processo viene terminato e l'operazione fallisce con un errore chiaro.
    /// </param>
    /// <exception cref="FileNotFoundException">Se svn.exe non viene trovato.</exception>
    public SvnService(string? svnExePath = null, IProcessRunner? processRunner = null, TimeSpan? timeout = null)
    {
        _svnExePath = svnExePath ?? SvnLocator.FindSvn() ?? throw new FileNotFoundException(
            "svn.exe non trovato. Installa Subversion e assicurati che sia nel PATH, " +
            "oppure installa TortoiseSVN con l'opzione 'command line client tools'.");
        _processRunner = processRunner ?? new ProcessRunner();
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct)
    {
        // svn status --xml restituisce un XML con tutti i file modificati.
        var xml = await RunSvnAsync(["status", "--xml"], workingCopyPath, ct);
        return ParseStatusXml(xml);
    }

    /// <summary>
    /// Converte l'XML di <c>svn status --xml</c> in elenco di modifiche.
    /// Ignorati ed external non sono modifiche e vengono scartati.
    /// </summary>
    internal static IReadOnlyList<SvnFileStatus> ParseStatusXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var results = new List<SvnFileStatus>();

        foreach (var entry in doc.Descendants("entry"))
        {
            var path = entry.Attribute("path")?.Value ?? string.Empty;
            var wcStatus = entry.Element("wc-status");
            var itemStatus = wcStatus?.Attribute("item")?.Value ?? "unknown";
            var propsStatus = wcStatus?.Attribute("props")?.Value ?? "none";
            var isTreeConflict = string.Equals(
                wcStatus?.Attribute("tree-conflicted")?.Value, "true", StringComparison.OrdinalIgnoreCase);

            var kind = ParseStatusKind(itemStatus);
            if (kind is SvnStatusKind.Ignored or SvnStatusKind.External)
                continue;

            // Quando cambia solo una proprietà (es. svn:ignore), svn status --xml
            // restituisce item="normal" e props="modified": va trattato come modifica.
            if (kind == SvnStatusKind.Normal && propsStatus == "modified")
                kind = SvnStatusKind.Modified;

            // Conflitto sulle proprietà o ad albero: per l'utente è comunque un conflitto da risolvere.
            if (propsStatus == "conflicted" || isTreeConflict)
                kind = SvnStatusKind.Conflicted;

            results.Add(new SvnFileStatus(path, kind, isTreeConflict));
        }

        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Come TortoiseSVN si committa <b>esattamente</b> ciò che è elencato: <c>--depth empty</c> evita che una
    /// cartella aggiunta si trascini dietro i figli non selezionati (che restano <c>A</c>). Per le cartelle
    /// nuove appena aggiunte per intero (<paramref name="includeAllUnder"/>) si elencano esplicitamente
    /// tutti gli elementi che contengono, così il commit resta uno solo e atomico.
    /// </remarks>
    public async Task<long?> CommitAsync(
        IEnumerable<string> paths, string message, CancellationToken ct, IEnumerable<string>? includeAllUnder = null)
    {
        var pathList = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pathList.Count == 0)
            return null;

        foreach (var folder in includeAllUnder ?? [])
        {
            var statusXml = await RunSvnAsync(["status", "--xml", Peg(folder)], workingDirectory: null, ct);
            foreach (var entry in XDocument.Parse(statusXml).Descendants("entry"))
            {
                var item = entry.Element("wc-status")?.Attribute("item")?.Value;
                var path = entry.Attribute("path")?.Value;
                if (path is not null && item is "added" or "deleted" or "modified" or "replaced")
                    pathList.Add(Path.GetFullPath(path));
            }
        }

        // Il messaggio passa da un file UTF-8 (-F --encoding utf-8): in riga di comando emoji/frecce/caratteri
        // fuori dalla codepage ANSI verrebbero persi, e la lunghezza sarebbe limitata.
        var messageFile = Path.Combine(Path.GetTempPath(), $"svnassist-msg-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(messageFile, message, new UTF8Encoding(false), ct);

            // Il commit può durare a lungo (molti file, server lento): niente timeout, l'utente può annullare.
            var output = await RunSvnWithTargetsAsync(
                ["commit", "--depth", "empty", "-F", messageFile, "--encoding", "utf-8"],
                pathList.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), workingDirectory: null, ct, applyTimeout: false);
            return ParseCommittedRevision(output);
        }
        finally
        {
            try { File.Delete(messageFile); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* idem */ }
        }
    }

    /// <summary>Estrae N da <c>Committed revision N.</c> (output in inglese, vedi <c>LC_ALL=C</c>).</summary>
    internal static long? ParseCommittedRevision(string output)
    {
        var match = Regex.Match(output, @"^Committed revision (\d+)\.", RegexOptions.Multiline);
        return match.Success
               && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rev)
            ? rev
            : null;
    }

    /// <inheritdoc />
    public async Task AddAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        await RunSvnWithTargetsAsync(["add", "--force"], pathList, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Senza <c>--force</c> svn rifiuta file con modifiche locali (E195006) e non versionati (E200005):
    /// il flag si passa solo dopo che l'utente ha confermato la perdita delle modifiche.
    /// </remarks>
    public async Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct, bool force = false)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        await RunSvnWithTargetsAsync(force ? ["delete", "--force"] : ["delete"], pathList, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task RevertAsync(IEnumerable<string> paths, CancellationToken ct, bool recursive = true)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        await RunSvnWithTargetsAsync(["revert", "--depth", recursive ? "infinity" : "empty"], pathList, workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task ResolveAsync(string path, SvnConflictChoice accept, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Il percorso non può essere vuoto.", nameof(path));

        // "working" = "ho sistemato io il file": se ci sono ancora i marcatori si committerebbe
        // codice rotto, quindi ci si ferma prima di dire a svn che è risolto.
        if (accept == SvnConflictChoice.Working && ContainsConflictMarkers(path))
            throw new InvalidOperationException(
                "Il file contiene ancora i marcatori di conflitto (<<<<<<<, =======, >>>>>>>). " +
                "Sistemalo a mano e rimuovili, oppure scegli di tenere la versione locale o quella del server.");

        var acceptValue = accept switch
        {
            SvnConflictChoice.Working => "working",
            SvnConflictChoice.MineFull => "mine-full",
            SvnConflictChoice.TheirsFull => "theirs-full",
            SvnConflictChoice.Base => "base",
            _ => throw new ArgumentOutOfRangeException(nameof(accept)),
        };

        await RunSvnAsync(["resolve", "--accept", acceptValue, Peg(path)], workingDirectory: null, ct);
    }

    /// <summary>
    /// <c>true</c> se il file di testo contiene righe che iniziano con un marcatore di conflitto di svn
    /// (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; </c> oppure <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; </c>).
    /// </summary>
    /// <remarks>
    /// Il solo <c>=======</c> non basta: è anche la sottolineatura dei titoli Markdown/reStructuredText
    /// e darebbe falsi positivi; in un vero conflitto compare sempre insieme agli altri due.
    /// Directory, file mancanti (conflitti ad albero) e binari non si controllano.
    /// </remarks>
    internal static bool ContainsConflictMarkers(string path)
    {
        if (!File.Exists(path) || IsBinaryFile(path))
            return false;

        // Bastano i primi caratteri di ogni riga: letture limitate, anche su un file enorme su una riga sola.
        return ReadLinesBounded(path, maxLineChars: 8).Any(line =>
            line.Text.StartsWith("<<<<<<< ", StringComparison.Ordinal) ||
            line.Text.StartsWith(">>>>>>> ", StringComparison.Ordinal));
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

        // Solo "proprietà non impostata" (W200017/E200017) significa lista vuota. Qualsiasi altro
        // errore va propagato: proseguire riscriverebbe svn:ignore perdendo le voci esistenti.
        var propget = await RunSvnRawAsync(["propget", "svn:ignore", parentDirectory], workingDirectory: null, ct);
        string existingIgnore;
        if (propget.ExitCode == 0)
            existingIgnore = propget.StandardOutput.EndsWith("\r\n") ? propget.StandardOutput[..^2]
                : propget.StandardOutput.EndsWith('\n') ? propget.StandardOutput[..^1]
                : propget.StandardOutput;
        else if (propget.StandardError.Contains("200017", StringComparison.Ordinal))
            existingIgnore = string.Empty;
        else
            throw new InvalidOperationException(BuildErrorMessage("propget", propget.ExitCode, propget.StandardError));

        // Le righe esistenti restano come sono (spazi, righe vuote, pattern altrui): si toglie solo il
        // newline finale che propget aggiunge.
        var entries = existingIgnore.Length == 0
            ? new List<string>()
            : existingIgnore.Split('\n').Select(v => v.TrimEnd('\r')).ToList();
        // svn canonicalizza svn:ignore con un LF finale (e propget ne stampa un altro): resta una voce vuota
        // in coda che non e' una riga dell'utente.
        if (entries.Count > 0 && entries[^1].Length == 0)
            entries.RemoveAt(entries.Count - 1);

        // svn:ignore è un elenco di glob: i metacaratteri nel nome ([ ] * ? \) vanno protetti, altrimenti
        // "a[1].txt" ignorerebbe a1.txt e non se stesso.
        var pattern = EscapeGlob(targetName);
        if (entries.Any(v => string.Equals(v, pattern, StringComparison.OrdinalIgnoreCase)))
            return;

        entries.Add(pattern);

        // Il valore multilinea è passato come singolo argomento: ArgumentList lo gestisce
        // correttamente, senza bisogno di escaping manuale delle virgolette/newline.
        var newIgnoreValue = string.Join("\n", entries);
        await RunSvnAsync(["propset", "svn:ignore", newIgnoreValue, parentDirectory], workingDirectory: null, ct);
    }

    /// <summary>Protegge con '\' i metacaratteri dei glob di svn:ignore (<c>\ [ ] * ?</c>).</summary>
    internal static string EscapeGlob(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (c is '\\' or '[' or ']' or '*' or '?')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<UpdateResult> UpdateAsync(string workingCopyPath, CancellationToken ct)
    {
        // L'update di una working copy grande può durare a lungo: niente timeout, l'utente può annullare.
        // Su un file si aggiorna solo quel file (cwd = la sua cartella); su una cartella, tutta la cartella.
        var isFile = File.Exists(workingCopyPath);
        var output = isFile
            ? await RunSvnAsync(["update", Peg(workingCopyPath)], Path.GetDirectoryName(workingCopyPath), ct, applyTimeout: false)
            : await RunSvnAsync(["update"], workingCopyPath, ct, applyTimeout: false);
        return ParseUpdateOutput(output);
    }

    /// <summary>
    /// Estrae revisione e conflitti dall'output (in inglese) di <c>svn update</c>:
    /// <c>Updated to revision N.</c> / <c>At revision N.</c> e le righe d'azione con <c>C</c>
    /// nelle colonne di stato (<c>C    file</c>; <c> C   file</c> per le proprietà; <c>   C file</c> ad albero).
    /// </summary>
    internal static UpdateResult ParseUpdateOutput(string output)
    {
        long? revision = null;
        foreach (Match m in Regex.Matches(output, @"^(?:Updated to|At) revision (\d+)\.", RegexOptions.Multiline))
        {
            // Gli external stampano "Updated external to revision N." (non combacia): l'ultima riga vince.
            if (long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rev))
                revision = rev;
        }

        var conflicts = new List<string>();
        foreach (Match m in Regex.Matches(output, @"^(?<cols>[ ADUCGEMR]{4}) (?<path>\S.*?)\s*$", RegexOptions.Multiline))
        {
            if (m.Groups["cols"].Value.Contains('C'))
                conflicts.Add(m.Groups["path"].Value);
        }

        return new UpdateResult(revision, conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct)
    {
        // svn log --xml -l N <target> restituisce le ultime N revisioni in XML.
        // Passiamo targetPath come argomento (supporta sia path locali che URL).
        var xml = await RunSvnAsync(
            ["log", "--xml", "-l", maxEntries.ToString(), SafeTarget(targetPath)], workingDirectory: null, ct);
        return ParseLogXml(xml);
    }

    /// <summary>
    /// Converte l'XML di <c>svn log --xml</c>. Le date ISO-8601 di svn si leggono sempre con la
    /// cultura invariante, qualunque sia la lingua di Windows.
    /// </summary>
    internal static IReadOnlyList<SvnLogEntry> ParseLogXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var results = new List<SvnLogEntry>();

        foreach (var entry in doc.Descendants("logentry"))
        {
            long.TryParse(
                entry.Attribute("revision")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision);
            var author = entry.Element("author")?.Value ?? "(sconosciuto)";
            // <date> può mancare (revprop cancellata): in quel caso resta la data di default.
            DateTimeOffset.TryParse(
                entry.Element("date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date);
            var message = entry.Element("msg")?.Value ?? string.Empty;

            results.Add(new SvnLogEntry(revision, author, date, message));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct)
    {
        // svn diff -c REVISION mostra le modifiche introdotte da quella revisione (unified diff).
        return await RunSvnAsync(["diff", "-c", revision.ToString(), SafeTarget(targetPath)], workingDirectory: null, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>-U 0</c> toglie il contesto e <c>-p</c> aggiunge il nome della funzione nell'intestazione
    /// delle hunk: l'AI vede solo le righe cambiate. NON si ignorano gli spazi (<c>-w</c>): anche
    /// una modifica di sola indentazione è una modifica da descrivere nel commit.
    /// </remarks>
    public async Task<string> GetDiffForAiAsync(
        IReadOnlyList<string> paths, string? workingCopyPath, CancellationToken ct)
    {
        if (paths.Count == 0)
            return string.Empty;

        // Con la working copy come cwd e path relativi, svn stampa "Index: src\A.cs" invece del
        // percorso assoluto: meno rumore (e meno token) per l'AI.
        var workingDirectory = string.IsNullOrWhiteSpace(workingCopyPath) ? null : workingCopyPath;
        var targets = paths.Select(p => (Full: p, Arg: ToSvnTarget(p, workingDirectory))).ToList();

        // svn diff non conosce i file non versionati (E155010): vanno separati prima.
        // Con -v svn elenca anche i versionati "normal": chi non compare (o è '?') è nuovo.
        // svn status non accetta --targets: la lista lunga si spezza in più invocazioni.
        var versioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in ChunkByCommandLine(["status", "--xml", "-v", "--depth", "empty"], targets.Select(t => Peg(t.Arg)).ToList()))
        {
            var statusXml = await RunSvnAsync(chunk, workingDirectory, ct);
            foreach (var entry in XDocument.Parse(statusXml).Descendants("entry"))
            {
                if (entry.Element("wc-status")?.Attribute("item")?.Value is not ("unversioned" or "ignored" or null))
                    versioned.Add(NormalizePath(entry.Attribute("path")?.Value ?? string.Empty, workingDirectory));
            }
        }

        var versionedTargets = targets.Where(t => versioned.Contains(NormalizePath(t.Arg, workingDirectory))).ToList();
        var newTargets = targets.Except(versionedTargets).ToList();

        var builder = new StringBuilder();
        if (versionedTargets.Count > 0)
        {
            // svn diff non accetta --targets (1.14): stessa strategia, spezzare in più invocazioni (concatenate).
            // Niente "@" finale qui: "svn diff nome@2x.png@" fallisce (E155010), mentre senza funziona.
            var diffText = new StringBuilder();
            foreach (var chunk in ChunkByCommandLine(
                         ["diff", "--depth", "empty", "-x", "-U 0 -p", "--no-diff-deleted"], versionedTargets.Select(t => t.Arg).ToList()))
            {
                var chunkOutput = await RunSvnAsync(chunk, workingDirectory, ct);
                if (diffText.Length > 0 && diffText[^1] != '\n' && chunkOutput.Length > 0)
                    diffText.Append('\n');
                diffText.Append(chunkOutput);
            }

            builder.Append(RestoreAnsiHeaders(diffText.ToString(), versionedTargets.Select(t => t.Arg)));
        }

        foreach (var (full, arg) in newTargets)
        {
            var preview = BuildNewFileDiff(full, arg);
            if (preview.Length == 0)
                continue;
            if (builder.Length > 0 && builder[^1] != '\n')
                builder.Append('\n');
            builder.Append(preview);
        }

        return builder.ToString();
    }

    /// <summary>Percorso relativo alla working copy se ci sta dentro, altrimenti quello originale.</summary>
    private static string ToSvnTarget(string fullPath, string? workingDirectory)
    {
        if (workingDirectory is null)
            return fullPath;

        var relative = Path.GetRelativePath(workingDirectory, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return fullPath;

        // Un nome che inizia con '-' verrebbe scambiato per un'opzione.
        return relative.StartsWith('-') ? Path.Combine(".", relative) : relative;
    }

    private static string NormalizePath(string path, string? workingDirectory)
        => Path.GetFullPath(path, workingDirectory ?? Directory.GetCurrentDirectory())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Diff unificato sintetico per un file/cartella nuovo non versionato (che <c>svn diff</c> non vede):
    /// solo le prime righe; binari e cartelle sono solo segnalati.
    /// </summary>
    /// <param name="fullPath">Percorso su disco.</param>
    /// <param name="displayPath">Nome da mostrare nell'intestazione.</param>
    /// <returns>Il diff, o stringa vuota se l'elemento non esiste più.</returns>
    internal static string BuildNewFileDiff(string fullPath, string displayPath)
    {
        if (Directory.Exists(fullPath))
            return $"Index: {displayPath}\n(nuova cartella non versionata)\n";

        if (!File.Exists(fullPath))
            return string.Empty;

        var length = new FileInfo(fullPath).Length;
        if (IsBinaryFile(fullPath))
            return $"Index: {displayPath}\n(nuovo file binario, {length} byte)\n";

        var shown = new List<string>();
        var omitted = 0;
        var truncated = false;
        // Su file enormi non si conta il totale: basta sapere che c'è dell'altro.
        var countAll = length <= NewFileCountLinesMaxBytes;

        foreach (var (text, lineTruncated) in ReadLinesBounded(fullPath, NewFileMaxLineChars))
        {
            if (shown.Count < NewFilePreviewLines)
            {
                // Righe lunghissime (minificati, base64) sprecherebbero il budget dell'AI.
                shown.Add(lineTruncated ? text + "..." : text);
                continue;
            }

            truncated = true;
            if (!countAll)
                break;
            omitted++;
        }

        var sb = new StringBuilder();
        sb.Append("Index: ").Append(displayPath).Append('\n');
        sb.Append("===================================================================\n");
        sb.Append("--- ").Append(displayPath).Append("\t(nonexistent)\n");
        sb.Append("+++ ").Append(displayPath).Append("\t(working copy)\n");
        if (shown.Count > 0)
        {
            sb.Append("@@ -0,0 +1,").Append(shown.Count).Append(" @@\n");
            foreach (var line in shown)
                sb.Append('+').Append(line).Append('\n');
        }

        if (truncated)
            sb.Append(countAll ? $"... ({omitted} righe omesse)\n" : "... (altre righe omesse)\n");

        return sb.ToString();
    }

    /// <summary>
    /// Legge le righe di un file di testo tenendo in memoria al più <paramref name="maxLineChars"/> caratteri per riga
    /// (il resto è scartato): un file enorme su una sola riga non occupa memoria. Riconosce \n, \r\n e \r.
    /// </summary>
    internal static IEnumerable<(string Text, bool Truncated)> ReadLinesBounded(string path, int maxLineChars)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var truncated = false;
        var anyChar = false;
        var previousWasCr = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (previousWasCr)
                {
                    previousWasCr = false;
                    if (c == '\n')
                        continue;
                }

                if (c is '\r' or '\n')
                {
                    yield return (line.ToString(), truncated);
                    line.Clear();
                    truncated = false;
                    anyChar = false;
                    previousWasCr = c == '\r';
                    continue;
                }

                anyChar = true;
                if (line.Length < maxLineChars)
                    line.Append(c);
                else
                    truncated = true;
            }
        }

        if (anyChar)
            yield return (line.ToString(), truncated);
    }

    /// <summary>Euristica standard: un byte NUL nei primi 8 KB indica un file binario.</summary>
    private static bool IsBinaryFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[BinarySniffBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    /// <inheritdoc />
    public async Task<string> GetFileDiffAsync(string filePath, CancellationToken ct)
    {
        // svn diff per un singolo file: modifiche locali non committate rispetto a BASE.
        return await RunSvnAsync(["diff", SafeTarget(filePath)], workingDirectory: null, ct);
    }

    internal static string BuildTortoiseDiffArguments(string filePath) => $"/command:diff /path:\"{filePath}\"";

    /// <inheritdoc />
    public async Task<bool> OpenNativeDiffAsync(string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Il percorso file non può essere vuoto.", nameof(filePath));

        ct.ThrowIfCancellationRequested();

        var tortoiseProcPath = SvnLocator.FindTortoiseProc();
        if (string.IsNullOrWhiteSpace(tortoiseProcPath))
            return false;

        // Stringa grezza: TortoiseProc vuole /path:"C:\a b\c" (virgolette attorno al solo valore), mentre
        // ArgumentList produrrebbe "/path:C:\a b\c" con i percorsi che contengono spazi.
        var startInfo = new ProcessStartInfo
        {
            FileName = tortoiseProcPath,
            Arguments = BuildTortoiseDiffArguments(filePath),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
    /// <param name="maxLevels">Numero massimo di livelli da risalire (default: fino alla radice del disco; una working copy ha un solo <c>.svn</c> in cima, anche con alberi più profondi di 10 livelli).</param>
    /// <returns>Il percorso della root SVN, oppure <c>null</c> se non trovata.</returns>
    public static string? FindSvnRoot(string startPath, int maxLevels = int.MaxValue)
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

    // ── Riga di comando lunga, peg revision, codepage ANSI ───────────

    /// <summary>
    /// Budget (caratteri) per gli argomenti in riga di comando: il limite di Windows è ~32.767 per l'intera
    /// riga, qui si lascia margine per svn.exe, le opzioni, il messaggio di commit e il quoting.
    /// </summary>
    internal const int MaxCommandLineChars = 24000;

    /// <summary>
    /// Un nome con <c>@</c> (es. <c>icona@2x.png</c>) verrebbe letto come "percorso@revisione":
    /// svn vuole una <c>@</c> finale per dire che non c'è alcuna peg revision.
    /// </summary>
    private static string Peg(string path) => path.Contains('@') ? path + "@" : path;

    /// <summary>Un percorso (o un URL) che inizia con '-' verrebbe letto da svn come opzione: lo si rende relativo ("./").</summary>
    private static string SafeTarget(string target)
        => target.StartsWith('-') ? "." + Path.DirectorySeparatorChar + target : target;

    private static int CommandLineLength(IEnumerable<string> args) => args.Sum(a => a.Length + 3);

    /// <summary>
    /// Spezza <paramref name="targets"/> in gruppi tali che <paramref name="head"/> + gruppo stia nel budget
    /// della riga di comando. Per i comandi che non conoscono <c>--targets</c> (status, diff).
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> ChunkByCommandLine(
        IReadOnlyList<string> head, IReadOnlyList<string> targets)
    {
        var budget = Math.Max(1000, MaxCommandLineChars - CommandLineLength(head));
        var current = new List<string>();
        var length = 0;
        foreach (var target in targets)
        {
            var cost = target.Length + 3;
            if (current.Count > 0 && length + cost > budget)
            {
                yield return [.. head, .. current];
                current.Clear();
                length = 0;
            }

            current.Add(target);
            length += cost;
        }

        if (current.Count > 0)
            yield return [.. head, .. current];
    }

    /// <summary>
    /// Esegue <paramref name="head"/> (sottocomando e opzioni) sui <paramref name="targets"/>. Se la riga di comando
    /// sarebbe troppo lunga i percorsi vanno in un file temporaneo passato con <c>--targets</c>.
    /// </summary>
    /// <remarks>
    /// Verificato su svn 1.14 per Windows: il file di <c>--targets</c> è letto nella <b>codepage ANSI</b> di sistema,
    /// non in UTF-8 (un percorso con accento scritto in UTF-8 non viene trovato). I percorsi non rappresentabili
    /// nella codepage restano sulla riga di comando.
    /// </remarks>
    private async Task<string> RunSvnWithTargetsAsync(
        IReadOnlyList<string> head, IReadOnlyList<string> targets, string? workingDirectory, CancellationToken ct,
        bool applyTimeout = true)
    {
        var pegged = targets.Select(Peg).ToList();
        if (CommandLineLength(head) + CommandLineLength(pegged) + _svnExePath.Length <= MaxCommandLineChars)
            return await RunSvnAsync([.. head, .. pegged], workingDirectory, ct, applyTimeout);

        var ansi = GetAnsiEncoding();
        var inFile = new List<string>();
        var inline = new List<string>();
        foreach (var target in pegged)
        {
            if (!target.Contains('\n') && !target.Contains('\r') && CanEncode(ansi, target))
                inFile.Add(target);
            else
                inline.Add(target);
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"svnassist-targets-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, string.Join("\n", inFile) + "\n", ansi, ct);
            return await RunSvnAsync([.. head, "--targets", tempFile, .. inline], workingDirectory, ct, applyTimeout);
        }
        finally
        {
            try { File.Delete(tempFile); }
            catch (IOException) { /* best effort: è un file temporaneo */ }
            catch (UnauthorizedAccessException) { /* idem */ }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>Codepage ANSI di sistema (quella in cui svn.exe legge <c>--targets</c> e scrive le intestazioni dei diff).</summary>
    internal static Encoding GetAnsiEncoding()
    {
        try
        {
            // Su .NET (Core) le codepage legacy (es. 1252) non sono disponibili finché non si registra il provider.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetACP(), EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true);
        }
    }

    private static bool CanEncode(Encoding encoding, string text)
    {
        try
        {
            encoding.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>svn diff</c> su Windows scrive le intestazioni (<c>Index:</c>, <c>---</c>, <c>+++</c>) con i percorsi nella
    /// codepage ANSI mentre il contenuto è UTF-8: letto tutto come UTF-8, <c>città.cs</c> diventa <c>citt?.cs</c>.
    /// Poiché i percorsi sono quelli passati dal chiamante, si rimappano i nomi storpiati su quelli noti
    /// (anche come prefisso, per le cartelle). Con codepage UTF-8 il nome storpiato coincide e non cambia nulla.
    /// </summary>
    internal static string RestoreAnsiHeaders(string diff, IEnumerable<string> targetPaths)
    {
        var ansi = GetAnsiEncoding();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targetPaths)
        {
            // svn stampa i percorsi relativi con '/' anche su Windows.
            var path = target.Replace('\\', '/');
            if (path.All(c => c < 128) || !CanEncode(ansi, path))
                continue;

            var garbled = Encoding.UTF8.GetString(ansi.GetBytes(path));
            if (garbled != path)
                map[garbled] = path;
        }

        if (map.Count == 0 || diff.Length == 0)
            return diff;

        var lines = diff.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var prefix = line.StartsWith("Index: ", StringComparison.Ordinal) ? "Index: "
                : line.StartsWith("--- ", StringComparison.Ordinal) ? "--- "
                : line.StartsWith("+++ ", StringComparison.Ordinal) ? "+++ "
                : null;
            if (prefix is null)
                continue;

            var rest = line[prefix.Length..];
            var end = rest.IndexOf('\t');
            if (end < 0)
                end = rest.EndsWith('\r') ? rest.Length - 1 : rest.Length;
            var shown = rest[..end];

            foreach (var (garbled, original) in map)
            {
                if (shown == garbled || shown.StartsWith(garbled + "/", StringComparison.Ordinal))
                {
                    lines[i] = prefix + original + shown[garbled.Length..] + rest[end..];
                    break;
                }
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Esegue un comando svn e restituisce stdout; un exit code != 0 diventa un'eccezione
    /// con messaggio comprensibile (vedi <see cref="BuildErrorMessage"/>).
    /// </summary>
    /// <param name="arguments">Argomenti del comando (uno per elemento).</param>
    /// <param name="workingDirectory">Directory di lavoro (null = corrente).</param>
    /// <param name="ct">Token di cancellazione.</param>
    /// <returns>L'output standard del comando.</returns>
    /// <exception cref="InvalidOperationException">Se il comando termina con errore o va in timeout.</exception>
    private async Task<string> RunSvnAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct, bool applyTimeout = true)
    {
        var result = await RunSvnRawAsync(arguments, workingDirectory, ct, applyTimeout);

        if (result.ExitCode != 0)
        {
            var subcommand = arguments.Count > 0 ? arguments[0] : "?";
            throw new InvalidOperationException(BuildErrorMessage(subcommand, result.ExitCode, result.StandardError));
        }

        return result.StandardOutput;
    }

    /// <summary>
    /// Unico punto in cui si lancia svn: aggiunge <c>--non-interactive</c> (niente prompt di credenziali
    /// o certificati su un processo senza console, che resterebbe appeso), forza l'output in inglese e
    /// applica il timeout. L'annullamento dell'utente (<paramref name="ct"/>) resta distinto dal timeout.
    /// </summary>
    /// <param name="applyTimeout">
    /// <c>false</c> per le operazioni lunghe avviate dall'utente (update, commit, in futuro switch/merge/checkout):
    /// non hanno un limite fisso, si fermano solo con l'annullamento dell'utente.
    /// </param>
    private async Task<ProcessResult> RunSvnRawAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct, bool applyTimeout = true)
    {
        // L'opzione va subito dopo il sottocomando: gli argomenti seguenti (path, messaggi) restano intatti.
        List<string> full = [arguments[0], "--non-interactive", .. arguments.Skip(1)];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (applyTimeout)
            timeoutCts.CancelAfter(_timeout);

        try
        {
            return await _processRunner.RunAsync(
                _svnExePath, full, workingDirectory, timeoutCts.Token, environment: SvnProcessEnvironment);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"svn {arguments[0]} non ha risposto entro {_timeout.TotalMinutes:0.#} minuti ed è stato interrotto. " +
                "Controlla la connessione al server e riprova.");
        }
    }

    /// <summary>
    /// Compone il messaggio d'errore: un suggerimento in italiano per i casi comuni, seguito
    /// sempre dal testo originale di svn (utile per il supporto).
    /// </summary>
    internal static string BuildErrorMessage(string subcommand, int exitCode, string standardError)
    {
        var detail = $"svn {subcommand} fallito (exit code {exitCode}): {standardError}";
        var hint = GetFriendlyHint(standardError);
        return hint is null ? detail : $"{hint}\n{detail}";
    }

    private static string? GetFriendlyHint(string stderr)
    {
        if (stderr.Contains("E170013", StringComparison.Ordinal))
            return "Impossibile collegarsi al server SVN: controlla la rete/VPN e l'indirizzo del repository.";

        if (stderr.Contains("E215004", StringComparison.Ordinal) || stderr.Contains("E170001", StringComparison.Ordinal))
            return "Autenticazione SVN mancante o rifiutata. Autenticati una volta con TortoiseSVN " +
                   "oppure da terminale con 'svn info <url-del-repository>' per salvare le credenziali, poi riprova.";

        if (stderr.Contains("E155004", StringComparison.Ordinal))
            return "La working copy è bloccata da un'operazione interrotta: esegui Cleanup (es. TortoiseSVN > Clean up) e riprova.";

        if (stderr.Contains("E155011", StringComparison.Ordinal) || stderr.Contains("E160024", StringComparison.Ordinal)
            || stderr.Contains("out of date", StringComparison.OrdinalIgnoreCase))
            return "Alcuni file sono più recenti sul server: fai prima Update, poi riprova il commit.";

        return null;
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
        "ignored" => SvnStatusKind.Ignored,
        "external" => SvnStatusKind.External,
        "obstructed" => SvnStatusKind.Obstructed,
        "incomplete" => SvnStatusKind.Incomplete,
        _ => SvnStatusKind.Unknown,
    };
}
