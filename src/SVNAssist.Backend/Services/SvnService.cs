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
    private readonly ISvnCredentialPrompt? _credentials;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private bool? _passwordFromStdin;

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
    /// <param name="credentials">
    /// Se presente e svn fallisce per autenticazione, si chiedono utente/password e il comando si ripete UNA volta con
    /// quelle credenziali (<c>--username</c> + <c>--password-from-stdin</c>). Null = nessun dialogo, errore normale.
    /// </param>
    /// <exception cref="FileNotFoundException">Se svn.exe non viene trovato.</exception>
    public SvnService(string? svnExePath = null, IProcessRunner? processRunner = null, TimeSpan? timeout = null,
        ISvnCredentialPrompt? credentials = null)
    {
        _credentials = credentials;
        _svnExePath = svnExePath ?? SvnLocator.FindSvn() ?? throw new FileNotFoundException(
            Loc.T("svn.exe non trovato. Installa Subversion e assicurati che sia nel PATH, " +
                  "oppure installa TortoiseSVN con l'opzione 'command line client tools'."));
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

            results.Add(new SvnFileStatus(path, kind, isTreeConflict, IsLocked: wcStatus?.Element("lock") is not null));
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
                Loc.T("Il file contiene ancora i marcatori di conflitto (<<<<<<<, =======, >>>>>>>). " +
                      "Sistemalo a mano e rimuovili, oppure scegli di tenere la versione locale o quella del server."));

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
            throw new InvalidOperationException(Loc.T("Impossibile determinare il nome dell'elemento da ignorare."));

        var parentDirectory = Directory.Exists(path)
            ? Directory.GetParent(path)?.FullName
            : Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new InvalidOperationException(Loc.T("Impossibile determinare la directory padre per svn:ignore."));

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
    public Task<UpdateResult> UpdateAsync(string workingCopyPath, CancellationToken ct)
        => UpdateCoreAsync(workingCopyPath, null, ct);

    /// <inheritdoc />
    public Task<UpdateResult> UpdateToRevisionAsync(string workingCopyPath, string revision, CancellationToken ct)
        => SvnRevision.TryNormalize(revision, out var normalized)
            ? UpdateCoreAsync(workingCopyPath, normalized, ct)
            : throw new ArgumentException(Loc.T("Revisione non valida: serve un numero intero positivo oppure HEAD."), nameof(revision));

    private async Task<UpdateResult> UpdateCoreAsync(string workingCopyPath, string? revision, CancellationToken ct)
    {
        // L'update di una working copy grande può durare a lungo: niente timeout, l'utente può annullare.
        // Su un file si aggiorna solo quel file (cwd = la sua cartella); su una cartella, tutta la cartella.
        IReadOnlyList<string> head = revision is null ? ["update"] : ["update", "-r", revision];
        var isFile = File.Exists(workingCopyPath);
        var output = isFile
            ? await RunSvnAsync([.. head, Peg(workingCopyPath)], Path.GetDirectoryName(workingCopyPath), ct, applyTimeout: false)
            : await RunSvnAsync(head, workingCopyPath, ct, applyTimeout: false);
        return ParseUpdateOutput(output);
    }

    /// <inheritdoc />
    public Task CleanupAsync(string path, CancellationToken ct) => CleanupAsync(path, CleanupOptions.Default, ct);

    /// <inheritdoc />
    public async Task CleanupAsync(string path, CleanupOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Il percorso non può essere vuoto.", nameof(path));

        // svn cleanup lavora su una cartella: per un file si usa quella che lo contiene. Può durare (rimuove lock, riprende operazioni).
        var dir = (File.Exists(path) ? Path.GetDirectoryName(path) : path)!;
        if (options.UsesExtras && !CleanupOptions.ExtrasSupported(await GetSvnVersionAsync(ct)))
            throw new InvalidOperationException(Loc.T("Queste opzioni del Cleanup richiedono svn 1.9 o successivo."));
        if (options.RunsCleanup)
            await RunSvnAsync(options.ToArgs(), dir, ct, applyTimeout: false);
        if (options.RevertAll)
            await RevertAsync([dir], ct, recursive: true);
    }

    /// <inheritdoc />
    public async Task LockAsync(IEnumerable<string> paths, string? message, bool force, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        List<string> head = force ? ["lock", "--force"] : ["lock"];
        // Come per il commit: il messaggio passa da un file UTF-8, non dalla riga di comando.
        string? messageFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                messageFile = Path.Combine(Path.GetTempPath(), $"svnassist-lock-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(messageFile, message, new UTF8Encoding(false), ct);
                head.AddRange(["-F", messageFile, "--encoding", "utf-8"]);
            }

            await RunSvnWithTargetsAsync(head, pathList, workingDirectory: null, ct);
        }
        finally
        {
            if (messageFile is not null)
            {
                try { File.Delete(messageFile); }
                catch (IOException) { /* best effort */ }
                catch (UnauthorizedAccessException) { /* idem */ }
            }
        }
    }

    /// <inheritdoc />
    public async Task UnlockAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        await RunSvnWithTargetsAsync(["unlock"], pathList, workingDirectory: null, ct);
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
    public Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct)
        => GetLogAsync(targetPath, maxEntries, new SvnLogFilter(), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, SvnLogFilter filter, CancellationToken ct)
    {
        var xml = await RunSvnAsync(BuildLogArgs(targetPath, maxEntries, filter), workingDirectory: null, ct);
        return ParseLogXml(xml).Where(filter.Matches).ToList();
    }

    /// <summary>
    /// Argomenti di <c>svn log</c>: le date diventano un intervallo di revisioni (<c>-r {A}:{B}</c>, dalla piu' recente),
    /// autore/testo/percorso un <c>--search</c> (svn &gt;= 1.8, glob; cerca anche in altri campi: e' un sovrainsieme,
    /// il filtro esatto lo fa <see cref="SvnLogFilter.Matches"/>). Con <c>-l</c> svn conta le revisioni gia' filtrate.
    /// </summary>
    internal static List<string> BuildLogArgs(string targetPath, int maxEntries, SvnLogFilter filter)
    {
        var args = new List<string> { "log", "--xml", "-v", "-l", maxEntries.ToString(CultureInfo.InvariantCulture) };
        static string D(DateOnly d, string time) => "{" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + time + "}";
        if (filter.From is { } f && filter.To is { } t)
            args.AddRange(["-r", D(t, "23:59:59") + ":" + D(f, "00:00:00")]);
        else if (filter.From is { } f2)
            args.AddRange(["-r", "HEAD:" + D(f2, "00:00:00")]);
        else if (filter.To is { } t2)
            args.AddRange(["-r", D(t2, "23:59:59") + ":1"]);

        var first = true;
        foreach (var term in new[] { filter.Author, filter.Text, filter.Path?.Replace('\\', '/') })
        {
            if (string.IsNullOrWhiteSpace(term))
                continue;
            args.Add(first ? "--search" : "--search-and");
            args.Add("*" + Regex.Replace(term.Trim(), @"([\\*?\[])", @"\$1") + "*");
            first = false;
        }

        args.Add(SafeTarget(targetPath));
        return args;
    }

    /// <inheritdoc />
    public async Task<string> GetRepositoryRootAsync(string target, CancellationToken ct)
        => (await RunSvnAsync(["info", "--show-item", "repos-root-url", Peg(SafeTarget(target))], workingDirectory: null, ct)).Trim();

    /// <inheritdoc />
    public async Task ExportRevisionAsync(string target, long revision, string destination, CancellationToken ct)
    {
        var rev = revision.ToString(CultureInfo.InvariantCulture);
        await RunSvnAsync(["export", "-r", rev, "--force", "--quiet", SafeTarget(target) + "@" + rev, destination], workingDirectory: null, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnChange>> GetChangedBetweenAsync(string target, long fromRevision, long toRevision, CancellationToken ct)
    {
        var xml = await RunSvnAsync(["diff", "--summarize", "--xml", "-r", $"{fromRevision}:{toRevision}", SafeTarget(target)], workingDirectory: null, ct);
        return ParseSummarizeXml(xml);
    }

    internal static IReadOnlyList<SvnChange> ParseSummarizeXml(string xml)
        => XDocument.Parse(xml).Descendants("path")
            .Where(p => p.Attribute("kind")?.Value == "file")
            .Select(p => new SvnChange(p.Attribute("item")?.Value switch { "added" => "A", "deleted" => "D", "replaced" => "R", _ => "M" }, p.Value))
            .ToList();

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

            var paths = entry.Element("paths")?.Elements("path")
                .Select(p => new SvnLogPath(p.Attribute("action")?.Value ?? "M", p.Value, p.Attribute("kind")?.Value ?? ""))
                .ToList() ?? [];

            results.Add(new SvnLogEntry(revision, author, date, message, paths));
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
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in ChunkByCommandLine(["status", "--xml", "-v", "--depth", "empty"], targets.Select(t => Peg(t.Arg)).ToList()))
        {
            var statusXml = await RunSvnAsync(chunk, workingDirectory, ct);
            foreach (var entry in XDocument.Parse(statusXml).Descendants("entry"))
            {
                var item = entry.Element("wc-status")?.Attribute("item")?.Value;
                if (item is not ("unversioned" or "ignored" or null))
                {
                    var key = NormalizePath(entry.Attribute("path")?.Value ?? string.Empty, workingDirectory);
                    versioned.Add(key);
                    items[key] = item == "normal" ? "M" : StatusLetter(item);
                }
            }
        }

        var versionedTargets = targets.Where(t => versioned.Contains(NormalizePath(t.Arg, workingDirectory))).ToList();
        var newTargets = targets.Except(versionedTargets).ToList();

        // Elenco file con stato in testa: svn diff non stampa nulla per un file aggiunto vuoto, copiato o con soli
        // cambi di proprieta', e un diff troncato perde i nomi. L'elenco garantisce che l'AI veda TUTTI i file selezionati.
        var builder = new StringBuilder(BuildStatusList(targets.Select(t =>
            (t.Arg, items.TryGetValue(NormalizePath(t.Arg, workingDirectory), out var letter) ? letter : "A"))));
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

    /// <summary>Lettera di stato come in <c>svn status</c> (A, M, D, R, C...).</summary>
    internal static string StatusLetter(string? item) => item switch
    {
        "added" => "A",
        "deleted" => "D",
        "replaced" => "R",
        "conflicted" => "C",
        "missing" => "!",
        "incomplete" => "!",
        _ => "M",
    };

    private const int MaxStatusListChars = 12_000;

    /// <summary>
    /// Elenco <c>"A  percorso"</c> dei file selezionati (i non versionati sono nuovi: A), in testa al diff.
    /// Limitato in dimensione: oltre il tetto si indica quanti file mancano.
    /// </summary>
    internal static string BuildStatusList(IEnumerable<(string Path, string Letter)> files)
    {
        var sb = new StringBuilder(Loc.T("Elenco file selezionati (stato svn):") + "\n");
        var shown = 0;
        var all = files.ToList();
        foreach (var (path, letter) in all)
        {
            var line = $"{letter}  {path.Replace('\\', '/')}\n";
            if (sb.Length + line.Length > MaxStatusListChars)
                break;
            sb.Append(line);
            shown++;
        }

        if (shown < all.Count)
            sb.Append(Loc.T("... (altri {0} file)", all.Count - shown) + "\n");
        return all.Count == 0 ? string.Empty : sb.Append('\n').ToString();
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
            return $"Index: {displayPath}\n{Loc.T("(nuova cartella non versionata)")}\n";

        if (!File.Exists(fullPath))
            return string.Empty;

        var length = new FileInfo(fullPath).Length;
        if (IsBinaryFile(fullPath))
            return $"Index: {displayPath}\n{Loc.T("(nuovo file binario, {0} byte)", length)}\n";

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
    public async Task ExportBaseAsync(string path, string destination, CancellationToken ct)
    {
        // svn export -r BASE da un percorso della working copy esporta la versione pristine (senza le modifiche locali).
        await RunSvnAsync(["export", "-r", "BASE", "--force", "--quiet", Peg(SafeTarget(path)), destination], workingDirectory: null, ct);
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

            return BranchLabel(relativeUrl);
        }
        catch
        {
            return string.Empty; // la UI mostra "—"
        }
    }

    /// <inheritdoc />
    public async Task<long> GetHeadRevisionAsync(string url, CancellationToken ct)
    {
        // RunSvnOnceAsync e non RunSvnRawAsync: un controllo in background non deve mai aprire il dialogo di login.
        var r = await RunSvnOnceAsync(["info", "--show-item", "last-changed-revision", Peg(SafeTarget(url))], null, ct, true, null);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(BuildErrorMessage("info", r.ExitCode, r.StandardError));
        return long.TryParse(r.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new InvalidOperationException("svn info: revisione non valida: " + r.StandardOutput.Trim());
    }

    /// <inheritdoc />
    public async Task<SvnInfo> GetInfoAsync(string workingCopyPath, CancellationToken ct)
        => ParseInfoXml(await RunSvnAsync(["info", "--xml"], workingCopyPath, ct));

    /// <summary>
    /// Converte l'XML di <c>svn info --xml</c>. <c>relative-url</c> esiste da svn 1.8: con una versione piu' vecchia
    /// si ricava dall'URL e dalla radice.
    /// </summary>
    internal static SvnInfo ParseInfoXml(string xml)
    {
        var e = XDocument.Parse(xml).Descendants("entry").FirstOrDefault()
                ?? throw new InvalidOperationException("svn info: risposta senza <entry>.");
        var url = e.Element("url")?.Value ?? string.Empty;
        var root = e.Element("repository")?.Element("root")?.Value ?? string.Empty;
        var rel = e.Element("relative-url")?.Value;
        if (string.IsNullOrEmpty(rel))
            rel = root.Length > 0 && url.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "^" + url[root.Length..] : url;

        static long? Num(string? s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        var commit = e.Element("commit");
        DateTimeOffset? date = DateTimeOffset.TryParse(commit?.Element("date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
        return new SvnInfo(url, rel, root, Num(e.Attribute("revision")?.Value) ?? 0, Num(commit?.Attribute("revision")?.Value),
            commit?.Element("author")?.Value, date);
    }

    private Version? _version;

    /// <inheritdoc />
    public async Task<Version?> GetSvnVersionAsync(CancellationToken ct)
    {
        if (_version is not null)
            return _version;
        try
        {
            var r = await _processRunner.RunAsync(_svnExePath, ["--version", "--quiet"], null, ct, environment: SvnProcessEnvironment);
            if (r.ExitCode == 0 && Version.TryParse(r.StandardOutput.Trim().Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var v))
                _version = v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // svn assente: versione sconosciuta, le altre operazioni falliranno con il loro errore.
        }

        return _version;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnListEntry>> ListEntriesAsync(string url, CancellationToken ct)
    {
        try
        {
            var xml = await RunSvnAsync(["list", "--xml", Peg(url)], workingDirectory: null, ct);
            return ParseListXml(xml);
        }
        catch (InvalidOperationException ex) when (IsPathNotFound(ex.Message))
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, SvnConflictInfo>> GetConflictInfoAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0)
            return new Dictionary<string, SvnConflictInfo>();
        try
        {
            var parsed = ParseConflictInfoXml(await RunSvnWithTargetsAsync(["info", "--xml"], paths, workingDirectory: null, ct));
            // Chiavi come i percorsi chiesti (svn li riscrive, qui si confrontano normalizzati).
            return paths.Where(p => parsed.ContainsKey(Path.GetFullPath(p)))
                .ToDictionary(p => p, p => parsed[Path.GetFullPath(p)], StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException ex)
        {
            // Un conflitto senza dati (svn vecchio, file sparito) non deve fermare il refresh: le azioni restano, senza i file di appoggio.
            BackendLog.Warn("svn info sui conflitti: " + ex.Message);
            return new Dictionary<string, SvnConflictInfo>();
        }
    }

    /// <summary>Elementi <c>&lt;entry&gt;</c> di <c>svn info --xml</c> con un <c>&lt;conflict&gt;</c>; i percorsi dei file di appoggio sono completi.</summary>
    internal static IReadOnlyDictionary<string, SvnConflictInfo> ParseConflictInfoXml(string xml)
    {
        var result = new Dictionary<string, SvnConflictInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in XDocument.Parse(xml).Descendants("entry"))
        {
            var conflict = entry.Element("conflict");
            var path = entry.Attribute("path")?.Value;
            if (conflict is null || string.IsNullOrEmpty(path))
                continue;
            string? File(string name) => conflict.Element(name)?.Value is { Length: > 0 } f ? Path.GetFullPath(f) : null;
            result[Path.GetFullPath(path)] = new SvnConflictInfo(conflict.Attribute("type")?.Value ?? "text",
                File("prev-base-file"), File("prev-wc-file"), File("cur-base-file"), File("prop-file"));
        }

        return result;
    }

    /// <summary>E170000 (URL inesistente), E200009 (alcuni target non esistono), "non-existent", "path not found".</summary>
    internal static bool IsPathNotFound(string? text)
        => !string.IsNullOrEmpty(text)
           && (text.Contains("E170000", StringComparison.Ordinal) || text.Contains("E200009", StringComparison.Ordinal)
               || text.Contains("non-existent", StringComparison.OrdinalIgnoreCase)
               || text.Contains("path not found", StringComparison.OrdinalIgnoreCase)
               || text.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase));

    /// <summary>Le sole cartelle di <c>svn list --xml</c> con revisione, autore e data dell'ultima modifica, in ordine alfabetico.</summary>
    internal static IReadOnlyList<SvnListEntry> ParseListXml(string xml)
        => XDocument.Parse(xml).Descendants("entry").Where(e => e.Attribute("kind")?.Value == "dir")
            .Select(e =>
            {
                var commit = e.Element("commit");
                return new SvnListEntry(e.Element("name")?.Value ?? string.Empty,
                    long.TryParse(commit?.Attribute("revision")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0,
                    commit?.Element("author")?.Value ?? string.Empty,
                    DateTimeOffset.TryParse(commit?.Element("date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null);
            })
            .Where(e => e.Name.Length > 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc />
    public async Task<UpdateResult> SwitchAsync(string workingCopyPath, string url, CancellationToken ct, long? revision = null)
        // Come l'update: puo' durare a lungo, niente timeout (l'utente puo' annullare).
        => ParseUpdateOutput(await RunSvnAsync(BuildSwitchArgs(url, revision), workingCopyPath, ct, applyTimeout: false));

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetEligibleRevisionsAsync(string workingCopyPath, string sourceUrl, CancellationToken ct)
        => MergeParser.ParseRevisions(await RunSvnAsync(["mergeinfo", "--show-revs", "eligible", Peg(sourceUrl)], workingCopyPath, ct));

    /// <inheritdoc />
    public async Task<IReadOnlyList<SvnLogEntry>> GetLogRangeAsync(string url, long min, long max, CancellationToken ct)
        => ParseLogXml(await RunSvnAsync(["log", "--xml", "-v", "-r", $"{max}:{min}", Peg(url)], workingDirectory: null, ct));

    /// <inheritdoc />
    public async Task<MergeOutput> MergeAsync(string workingCopyPath, string sourceUrl, IReadOnlyList<long>? revisions, bool dryRun, CancellationToken ct)
        // Puo' durare a lungo su repository lenti: niente timeout (l'utente puo' annullare).
        => MergeParser.ParseMerge(await RunSvnAsync(BuildMergeArgs(sourceUrl, revisions, dryRun), workingCopyPath, ct, applyTimeout: false));

    /// <summary><c>--accept postpone</c>: nessun editor, i conflitti restano nel pannello Changes. <c>--non-interactive</c> lo aggiunge RunSvnOnceAsync.</summary>
    internal static List<string> BuildMergeArgs(string sourceUrl, IReadOnlyList<long>? revisions, bool dryRun)
    {
        var args = new List<string> { "merge" };
        if (dryRun)
            args.Add("--dry-run");
        args.AddRange(["--accept", "postpone"]);
        if (revisions is { Count: > 0 })
            args.AddRange(["-c", string.Join(",", revisions.Select(r => r.ToString(CultureInfo.InvariantCulture)))]);
        args.Add(Peg(sourceUrl));
        return args;
    }

    /// <summary><c>--accept postpone</c>: i conflitti non aprono nulla, restano nel pannello Changes. <c>--non-interactive</c> lo aggiunge RunSvnOnceAsync.</summary>
    internal static List<string> BuildSwitchArgs(string url, long? revision = null)
        => revision is { } r ? ["switch", "--accept", "postpone", "-r", r.ToString(CultureInfo.InvariantCulture), Peg(url)] : ["switch", "--accept", "postpone", Peg(url)];

    /// <summary>Nome da mostrare per l'URL relativo (<c>^/trunk</c> -> <c>trunk</c>); la radice del repository (repo senza trunk/branches) e' "/ (radice)".</summary>
    internal static string BranchLabel(string relativeUrl)
    {
        var url = Uri.UnescapeDataString(relativeUrl.Trim()).TrimStart('^', '/').TrimEnd('/');
        return url.Length == 0 ? "/ (radice)" : url;
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

    private async Task<ProcessResult> RunSvnRawAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct, bool applyTimeout = true)
    {
        var result = await RunSvnOnceAsync(arguments, workingDirectory, ct, applyTimeout, null);
        if (result.ExitCode == 0 || _credentials is null || !SvnErrors.IsAuthenticationError(result.StandardError))
            return result;

        // Errore di autenticazione: si chiede utente/password e si ripete UNA volta (mai in ciclo).
        var credentials = await AskCredentialsAsync(result.StandardError, ct);
        return credentials is null ? result : await RunSvnOnceAsync(arguments, workingDirectory, ct, applyTimeout, credentials);
    }

    private async Task<SvnCredentials?> AskCredentialsAsync(string stderr, CancellationToken ct)
    {
        // Un dialogo alla volta: due comandi in parallelo non ne aprono due.
        await _credentialGate.WaitAsync(ct);
        try
        {
            var firstLine = stderr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            return await _credentials!.RequestAsync(firstLine, ct);
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    /// <summary>svn >= 1.10 legge la password da stdin (<c>--password-from-stdin</c>): non finisce nella riga di comando.</summary>
    internal static bool SupportsPasswordFromStdin(string versionOutput)
        => Version.TryParse(versionOutput.Trim().Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var v)
           && (v.Major > 1 || (v.Major == 1 && v.Minor >= 10));

    private async Task<bool> PasswordFromStdinAsync(CancellationToken ct)
    {
        if (_passwordFromStdin is { } known)
            return known;

        try
        {
            var r = await _processRunner.RunAsync(_svnExePath, ["--version", "--quiet"], null, ct, environment: SvnProcessEnvironment);
            _passwordFromStdin = r.ExitCode == 0 && SupportsPasswordFromStdin(r.StandardOutput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _passwordFromStdin = false;
        }

        return _passwordFromStdin.Value;
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
    /// <param name="credentials">Solo per il secondo tentativo dopo un errore di autenticazione.</param>
    private async Task<ProcessResult> RunSvnOnceAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct, bool applyTimeout,
        SvnCredentials? credentials)
    {
        // L'opzione va subito dopo il sottocomando: gli argomenti seguenti (path, messaggi) restano intatti.
        List<string> full = [arguments[0], "--non-interactive"];
        string? stdin = null;
        if (credentials is not null)
        {
            full.AddRange(["--username", credentials.UserName]);
            if (await PasswordFromStdinAsync(ct))
            {
                full.Add("--password-from-stdin");
                stdin = credentials.Password + "\n";
            }
            else
            {
                // svn < 1.10: la password può solo stare sulla riga di comando (visibile ai processi locali per qualche istante).
                BackendLog.Warn("svn < 1.10: password passata con --password (aggiorna svn per usare --password-from-stdin).");
                full.AddRange(["--password", credentials.Password]);
            }

            if (!credentials.Remember)
                full.Add("--no-auth-cache");
        }

        full.AddRange(arguments.Skip(1));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (applyTimeout)
            timeoutCts.CancelAfter(_timeout);

        try
        {
            if (SvnProgress.Sink is not { } sink)
                return await _processRunner.RunAsync(_svnExePath, full, workingDirectory, timeoutCts.Token, stdin, SvnProcessEnvironment);
            if (_processRunner is ILineProcessRunner streaming)
                return await streaming.RunStreamingAsync(_svnExePath, full, workingDirectory, timeoutCts.Token, stdin, SvnProcessEnvironment, sink);
            // Runner senza streaming (test): le righe arrivano tutte alla fine.
            var done = await _processRunner.RunAsync(_svnExePath, full, workingDirectory, timeoutCts.Token, stdin, SvnProcessEnvironment);
            foreach (var l in done.StandardOutput.Split('\n'))
                if (l.Trim().Length > 0)
                    sink(l.TrimEnd('\r'));
            return done;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                Loc.T("svn {0} non ha risposto entro {1:0.#} minuti ed è stato interrotto. " +
                      "Controlla la connessione al server e riprova.", arguments[0], _timeout.TotalMinutes));
        }
    }

    /// <summary>
    /// Compone il messaggio d'errore: un suggerimento in italiano per i casi comuni, seguito
    /// sempre dal testo originale di svn (utile per il supporto).
    /// </summary>
    internal static string BuildErrorMessage(string subcommand, int exitCode, string standardError)
    {
        var detail = Loc.T("svn {0} fallito (exit code {1}): {2}", subcommand, exitCode, standardError);
        var hint = GetFriendlyHint(standardError);
        return hint is null ? detail : $"{hint}\n{detail}";
    }

    private static string? GetFriendlyHint(string stderr)
    {
        if (stderr.Contains("E170013", StringComparison.Ordinal))
            return Loc.T("Impossibile collegarsi al server SVN: controlla la rete/VPN e l'indirizzo del repository.");

        if (stderr.Contains("E215004", StringComparison.Ordinal) || stderr.Contains("E170001", StringComparison.Ordinal))
            return Loc.T("Autenticazione SVN mancante o rifiutata: riprova e inserisci utente e password quando richiesto.");

        if (stderr.Contains("E155004", StringComparison.Ordinal))
            return Loc.T("La working copy è bloccata da un'operazione interrotta: esegui SVN Cleanup e riprova.");

        if (stderr.Contains("E155011", StringComparison.Ordinal) || stderr.Contains("E160024", StringComparison.Ordinal)
            || stderr.Contains("out of date", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Alcuni file sono più recenti sul server: fai prima Update, poi riprova il commit.");

        if (stderr.Contains("E195020", StringComparison.Ordinal) || stderr.Contains("mixed-revision", StringComparison.OrdinalIgnoreCase))
            return Loc.T("La working copy è a revisioni miste: esegui SVN Update e riprova.");

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
