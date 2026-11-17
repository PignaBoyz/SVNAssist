namespace SVNAssist.Services;

/// <summary>D10: rileva i marcatori di conflitto lasciati in un file (riga che inizia con 7 "&lt;", poi una riga di 7 "=", poi 7 "&gt;").</summary>
internal static class ConflictMarkers
{
    /// <summary>shortcut: oltre questa soglia il file non si legge (si assume che non sia un sorgente), alzala se serve.</summary>
    internal const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Binari, enormi, assenti o illeggibili: falso.</summary>
    internal static bool HasMarkers(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
                return false;
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
                return false; // binario
            var stage = 0; // 0 cerca <<<<<<<, 1 cerca =======, 2 cerca >>>>>>>
            // Rientri tollerati: l'editor di VS reindenta quando si incolla o si risolve a mano.
            foreach (var raw in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var line = raw.TrimStart();
                if (stage == 0 && line.StartsWith("<<<<<<<", StringComparison.Ordinal)) stage = 1;
                else if (stage == 1 && line.StartsWith("=======", StringComparison.Ordinal)) stage = 2;
                else if (stage == 2 && line.StartsWith(">>>>>>>", StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Bug 5 "Usa entrambe": ricostruisce il testo tenendo, per ogni blocco <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> / <c>=======</c> / <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>,
    /// entrambi i lati (mia poi loro, o loro poi mia) e lasciando invariato il resto. Marcatori rientrati o con testo dopo sono riconosciuti
    /// (come in <see cref="HasMarkers"/>); lo stile diff3 (<c>|||||||</c>, base) scarta la parte base. Gli a-capo (CRLF/LF) sono quelli del file.
    /// Restituisce <c>null</c> se non c'e' nessun blocco o se i marcatori sono sbilanciati (nulla da fare in modo sicuro).
    /// </summary>
    internal static string? TakeBoth(string text, bool mineFirst, out int blocks)
    {
        blocks = 0;
        var sb = new System.Text.StringBuilder(text.Length);
        var mine = new List<string>();
        var theirs = new List<string>();
        var stage = 0; // 0 fuori, 1 mia, 2 base (diff3), 3 loro
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl + 1;
            var raw = text.Substring(start, end - start);
            start = end;
            var t = raw.TrimStart();
            switch (stage)
            {
                case 0:
                    if (t.StartsWith("<<<<<<<", StringComparison.Ordinal)) { stage = 1; mine.Clear(); theirs.Clear(); }
                    else if (t.StartsWith(">>>>>>>", StringComparison.Ordinal) || t.StartsWith("|||||||", StringComparison.Ordinal)) return null;
                    else sb.Append(raw);
                    break;
                case 1:
                    if (t.StartsWith("|||||||", StringComparison.Ordinal)) stage = 2;
                    else if (t.StartsWith("=======", StringComparison.Ordinal)) stage = 3;
                    else if (t.StartsWith("<<<<<<<", StringComparison.Ordinal) || t.StartsWith(">>>>>>>", StringComparison.Ordinal)) return null;
                    else mine.Add(raw);
                    break;
                case 2:
                    if (t.StartsWith("=======", StringComparison.Ordinal)) stage = 3;
                    else if (t.StartsWith("<<<<<<<", StringComparison.Ordinal) || t.StartsWith(">>>>>>>", StringComparison.Ordinal)) return null;
                    break;
                default:
                    if (t.StartsWith(">>>>>>>", StringComparison.Ordinal))
                    {
                        // L'ultima riga di un lato e' sempre seguita da un marcatore, quindi ha il suo a-capo.
                        foreach (var l in mineFirst ? mine : theirs) sb.Append(l);
                        foreach (var l in mineFirst ? theirs : mine) sb.Append(l);
                        blocks++;
                        stage = 0;
                    }
                    else if (t.StartsWith("<<<<<<<", StringComparison.Ordinal)) return null;
                    else theirs.Add(raw);
                    break;
            }
        }

        return stage != 0 || blocks == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Riscrive il file con <see cref="TakeBoth"/> (lettura/scrittura byte per byte in Latin-1: BOM, codifica e a-capo restano identici).
    /// Falso, senza toccare il file, se e' binario, troppo grande, illeggibile o senza blocchi ben formati.
    /// </summary>
    internal static bool TryTakeBoth(string path, bool mineFirst, out int blocks)
    {
        blocks = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
                return false;
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
                return false;
            var result = TakeBoth(System.Text.Encoding.Latin1.GetString(bytes), mineFirst, out blocks);
            if (result is null)
                return false;
            File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(result));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
