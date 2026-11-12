using System.Text;
using System.Text.RegularExpressions;

namespace SVNAssist.Services;

/// <summary>
/// Annullamento di singole righe (come "Revert selected changes" di Git): dato il testo di lavoro, il testo BASE e
/// l'elenco dei blocchi del <c>svn diff -U0</c>, riporta alla versione BASE solo i blocchi che toccano le righe
/// selezionate. Classe pura (nessun I/O): il testo mantiene le proprie fine riga.
/// shortcut: i blocchi modificati si accoppiano riga per riga (i-esima nuova con i-esima vecchia); upgrade se serve un allineamento piu' fine.
/// </summary>
internal static class LineRevert
{
    /// <summary>Un blocco di <c>@@ -OldStart,OldCount +NewStart,NewCount @@</c> (conteggio 0: la riga indicata e' quella PRIMA del punto).</summary>
    internal readonly record struct Hunk(int OldStart, int OldCount, int NewStart, int NewCount);

    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Blocchi di modifica del diff unified (con qualunque contesto): ogni serie contigua di righe -/+ e' un blocco.</summary>
    internal static List<Hunk> ParseHunks(string unifiedDiff)
    {
        var result = new List<Hunk>();
        int o = 0, n = 0, os = 0, ns = 0, oc = 0, nc = 0;
        bool inHunk = false;
        void Flush()
        {
            if (oc + nc > 0) result.Add(new Hunk(oc > 0 ? os : os - 1, oc, nc > 0 ? ns : ns - 1, nc));
            oc = nc = 0;
        }
        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            var m = HunkHeader.Match(l);
            if (m.Success)
            {
                Flush();
                o = int.Parse(m.Groups[1].Value);
                n = int.Parse(m.Groups[3].Value);
                inHunk = true;
                continue;
            }
            if (l.StartsWith("Property changes on:")) break; // le modifiche alle proprieta' non sono righe di testo
            if (!inHunk) continue;
            char c = l.Length > 0 ? l[0] : ' ';
            if (c == '\\') continue; // "\ No newline at end of file"
            if (c == '-' || c == '+')
            {
                if (oc + nc == 0) { os = o; ns = n; }
                if (c == '-') { oc++; o++; } else { nc++; n++; }
            }
            else { Flush(); o++; n++; }
        }
        Flush();
        return result;
    }

    /// <summary>Righe con la propria fine riga (l'ultima senza newline resta senza).</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text.Substring(start, i - start + 1)); start = i + 1; }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary>
    /// Annulla le modifiche che toccano le righe <paramref name="from"/>..<paramref name="to"/> (1-based, incluse, del file di lavoro).
    /// <c>Count</c> = righe annullate (0 se la selezione non tocca modifiche: <c>Text</c> resta invariato).
    /// </summary>
    internal static (string Text, int Count) Apply(string work, string baseText, IReadOnlyList<Hunk> hunks, int from, int to)
    {
        var w = SplitLines(work);
        var b = SplitLines(baseText);
        var eol = work.Contains("\r\n") || baseText.Contains("\r\n") ? "\r\n" : "\n";
        var output = new List<string>();
        int count = 0, pos = 1; // pos = prossima riga di lavoro da copiare

        void CopyUntil(int line) // copia le righe di lavoro [pos, line)
        {
            for (; pos < line && pos <= w.Count; pos++) output.Add(w[pos - 1]);
        }

        foreach (var h in hunks.OrderBy(h => h.NewStart))
        {
            if (h.NewCount == 0)
            {
                // cancellazione pura: tra la riga NewStart e NewStart+1; toccata se la selezione la sfiora
                if (!(from <= h.NewStart + 1 && to >= h.NewStart)) continue;
                CopyUntil(h.NewStart + 1);
                for (int i = 0; i < h.OldCount; i++) output.Add(b[h.OldStart - 1 + i]);
                count += h.OldCount;
                continue;
            }

            int ns = h.NewStart, ne = h.NewStart + h.NewCount - 1;
            if (to < ns || from > ne) continue;
            CopyUntil(ns);
            for (int i = 0; i < h.NewCount; i++)
            {
                int line = ns + i;
                if (line < from || line > to) { output.Add(w[line - 1]); continue; }
                count++;
                if (i < h.OldCount) output.Add(b[h.OldStart - 1 + i]);
                if (i == h.NewCount - 1)
                    for (int k = h.NewCount; k < h.OldCount; k++) output.Add(b[h.OldStart - 1 + k]); // vecchie righe in piu': agganciate all'ultima
            }
            pos = ne + 1;
        }
        if (count == 0) return (work, 0);
        CopyUntil(w.Count + 1);

        // ogni riga che non e' l'ultima deve finire con newline (una riga BASE senza newline finale puo' finire in mezzo)
        for (int i = 0; i < output.Count - 1; i++)
            if (!output[i].EndsWith('\n')) output[i] += eol;
        return (string.Concat(output), count);
    }

    /// <summary>Decodifica rispettando il BOM; senza BOM prova UTF-8 rigoroso, poi Windows-1252. Restituisce anche encoding e BOM per riscrivere uguale.</summary>
    internal static (string Text, Encoding Enc, byte[] Bom) Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(data, 3, data.Length - 3), new UTF8Encoding(false), data[..3]);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return (new UnicodeEncoding(false, false).GetString(data, 2, data.Length - 2), new UnicodeEncoding(false, false), data[..2]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return (new UnicodeEncoding(true, false).GetString(data, 2, data.Length - 2), new UnicodeEncoding(true, false), data[..2]);
        try { return (new UTF8Encoding(false, true).GetString(data), new UTF8Encoding(false), []); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var e = Encoding.GetEncoding(1252);
            return (e.GetString(data), e, []);
        }
    }
}
