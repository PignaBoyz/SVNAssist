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
}
