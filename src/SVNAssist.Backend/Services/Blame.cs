using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace SVNAssist.Services;

/// <summary>
/// "Chi ha toccato la riga" nell'editor: <c>svn blame --xml</c> del file, una volta, poi in cache finche' la revisione della
/// working copy del file non cambia (update/commit). Le righe del file di lavoro si riportano sulla BASE con il diff locale:
/// quelle aggiunte o modificate e non committate non hanno autore (revisione 0).
/// Lo usa l'host solo quando l'utente lo accende: nessun lavoro in background.
/// </summary>
internal sealed class Blame
{
    /// <summary>Una riga della BASE: revisione, autore, data (null se svn non la da').</summary>
    internal readonly record struct Line(long Rev, string Author, DateTimeOffset? Date);

    private readonly ISvnService _svn;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Rev, Line[] Lines)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string File, long Rev), string> _messages = new();
    private const int MaxFiles = 16; // file con blame in cache: oltre, si svuota (memoria contenuta)

    public Blame(ISvnService svn) => _svn = svn;

    /// <summary>Righe di <c>svn blame --xml</c>, in ordine (indice 0 = riga 1).</summary>
    internal static Line[] Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants("entry")
            .OrderBy(e => int.Parse((string?)e.Attribute("line-number") ?? "0", CultureInfo.InvariantCulture))
            .Select(e =>
            {
                var c = e.Element("commit");
                if (c is null)
                    return new Line(0, string.Empty, null);
                var rev = long.Parse((string?)c.Attribute("revision") ?? "0", CultureInfo.InvariantCulture);
                var date = DateTimeOffset.TryParse((string?)c.Element("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTimeOffset?)null;
                return new Line(rev, (string?)c.Element("author") ?? string.Empty, date);
            })
            .ToArray();
    }

    /// <summary>
    /// Per ogni riga del file di lavoro (indice 0 = riga 1) la riga di blame, o null se e' una modifica locale non committata.
    /// <paramref name="workingLines"/> = righe del file su disco; <paramref name="localDiff"/> = <c>svn diff</c> del file (vuoto se non modificato).
    /// </summary>
    internal static Line?[] Map(Line[] baseLines, string localDiff, int workingLines)
    {
        var hunks = LineRevert.ParseHunks(localDiff);
        var result = new Line?[workingLines];
        for (int i = 0; i < workingLines; i++)
        {
            var (touched, old) = LineHistory.MapBack(hunks, i + 1);
            result[i] = !touched && old is { } o && o >= 1 && o <= baseLines.Length ? baseLines[o - 1] : null;
        }

        return result;
    }

    /// <summary>
    /// JSON per l'host: <c>{"revs":[{"r":14121,"a":"szoia","d":"2026-11-23"}], "lines":[0,0,-1,...]}</c> dove lines[i] e' l'indice
    /// in revs della riga i+1 (-1 = modifica locale). Oppure <c>{"error":"..."}</c> (file non versionato, binario...).
    /// </summary>
    public async Task<string> GetJsonAsync(string file, CancellationToken ct)
    {
        try
        {
            var info = await _svn.GetInfoAsync(file, ct);
            Line[] baseLines;
            lock (_lock)
                baseLines = _cache.TryGetValue(file, out var c) && c.Rev == info.Revision ? c.Lines : [];
            if (baseLines.Length == 0)
            {
                baseLines = Parse(await _svn.GetBlameXmlAsync(file, ct));
                lock (_lock)
                {
                    if (_cache.Count >= MaxFiles)
                        _cache.Clear();
                    _cache[file] = (info.Revision, baseLines);
                }
            }

            var working = await File.ReadAllLinesAsync(file, ct);
            var mapped = Map(baseLines, await _svn.GetFileDiffAsync(file, ct), working.Length);
            return ToJson(mapped);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Info("Blame: " + ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    internal static string ToJson(Line?[] lines)
    {
        var revs = new List<Line>();
        var index = new Dictionary<long, int>();
        var idx = new int[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] is not { } l)
            {
                idx[i] = -1;
                continue;
            }

            if (!index.TryGetValue(l.Rev, out var k))
            {
                k = revs.Count;
                index[l.Rev] = k;
                revs.Add(l);
            }

            idx[i] = k;
        }

        return JsonSerializer.Serialize(new
        {
            revs = revs.Select(r => new { r = r.Rev, a = r.Author, d = r.Date?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty }),
            lines = idx,
        });
    }

    /// <summary>Prima riga del messaggio della revisione (per il testo accanto all'autore), in cache. Vuoto se non si legge.</summary>
    public async Task<string> GetMessageAsync(string file, long rev, CancellationToken ct)
    {
        lock (_lock)
            if (_messages.TryGetValue((file, rev), out var m))
                return m;
        try
        {
            var entries = await _svn.GetLogAsync(file, 1, new Models.SvnLogFilter(Revisions: [rev]), ct);
            var msg = (entries.FirstOrDefault()?.Message ?? string.Empty).Replace("\r", string.Empty).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            lock (_lock)
                _messages[(file, rev)] = msg;
            return msg;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Info("Blame, messaggio r" + rev + ": " + ex.Message);
            return string.Empty;
        }
    }
}
