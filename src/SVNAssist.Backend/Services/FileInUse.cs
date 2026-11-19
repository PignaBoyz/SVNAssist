using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SVNAssist.Services;

/// <summary>
/// Cleanup/revert che falliscono perche' Windows non lascia a svn spostare un file tenuto aperto da un altro programma
/// (di solito Visual Studio, per esempio un .aps o un .rc aperto): E720005 "Can't move ... Access is denied", E155009 coda di lavoro non eseguita.
/// Riconosce il caso, trova il file e (se Windows lo dice) il programma che lo tiene aperto, e costruisce un messaggio che spiega cosa fare.
/// </summary>
internal static partial class FileInUse
{
    [GeneratedRegex(@"to\s+'(?<f>[^']+)'\s*:\s*(?:Access is denied|The process cannot access)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MoveTarget();

    [GeneratedRegex(@"'(?<f>[^']+)'\s*:\s*(?:Access is denied|The process cannot access the file)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnyTarget();

    /// <summary>Errore di svn dovuto a un file in uso; <paramref name="file"/> e' il percorso se riconoscibile (puo' restare vuoto).</summary>
    public static bool TryParse(string? text, out string file)
    {
        file = string.Empty;
        if (string.IsNullOrEmpty(text))
            return false;
        var denied = text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase);
        if (!denied && !text.Contains("E720005", StringComparison.Ordinal))
            return false;
        var m = MoveTarget().Match(text);
        if (!m.Success)
            m = AnyTarget().Match(text);
        if (m.Success)
            file = m.Groups["f"].Value;
        return true;
    }

    /// <summary>Messaggio per l'utente: cosa e' successo, chi tiene il file (se noto) e i passi per proseguire.</summary>
    public static string Explain(string file, string svnMessage)
    {
        var name = file.Length > 0 ? Path.GetFileName(file) : Loc.T("un file");
        var holders = file.Length > 0 ? Holders(file) : [];
        var by = holders.Count > 0 ? Loc.T(" (aperto da: {0})", string.Join(", ", holders)) : string.Empty;
        // Dettaglio tecnico di svn senza la riga di suggerimento gia' anteposta da BuildErrorMessage, su una riga e limitato.
        var detail = string.Join(" ", svnMessage.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Windows ", StringComparison.Ordinal)));
        var first = detail.Length > 400 ? detail[..400] + "…" : detail;
        return Loc.T("Cleanup non completato: \"{0}\" è in uso{1} e Windows non permette a svn di sostituirlo. Per proseguire: chiudi quel file in Visual Studio (o chiudi la solution) e rilancia il Cleanup; se non basta, chiudi Visual Studio e fai il Cleanup da SVNAssist riaperto o con TortoiseSVN. Nulla è stato perso: la working copy resta com'era. Dettaglio: {2}", name, by, first);
    }

    /// <summary>Programmi che tengono aperto il file (Restart Manager di Windows); vuoto se non si sa o fuori da Windows.</summary>
    internal static List<string> Holders(string file)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows())
            return result;
        uint session = 0;
        try
        {
            if (RmStartSession(out session, 0, new StringBuilder(33)) != 0)
                return result;
            if (RmRegisterResources(session, 1, [file], 0, IntPtr.Zero, 0, null) != 0)
                return result;
            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(session, out needed, ref count, null, out reasons);
            if (rc != 234 /* ERROR_MORE_DATA */ || needed == 0)
                return result;
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, out reasons) != 0)
                return result;
            for (var i = 0; i < count; i++)
                if (!string.IsNullOrWhiteSpace(info[i].AppName) && !result.Contains(info[i].AppName))
                    result.Add(info[i].AppName);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException)
        {
            // informazione solo di aiuto
        }
        finally
        {
            if (session != 0)
                _ = RmEndSession(session);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, out uint reasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);
}
