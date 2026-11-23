using System.IO;
using System.Text;

namespace SVNAssist;

/// <summary>
/// Log su file in <c>%LOCALAPPDATA%\SVNAssist\logs\backend-aaaammgg.log</c>. MAI su stdout (e' il canale JSON-RPC).
/// Best effort: un errore di scrittura non deve mai far cadere il backend.
/// Non vanno mai scritti segreti (API key, token) ne' contenuto dei diff.
/// </summary>
internal static class BackendLog
{
    private static readonly object Gate = new();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly int Pid = Environment.ProcessId;
    private static string? _directory;

    /// <summary>Cartella dei log (sovrascrivibile dai test).</summary>
    internal static string LogDirectory
    {
        get => _directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "logs");
        set => _directory = value;
    }

    internal static void Info(string message) => Write("INFO", message);

    internal static void Warn(string message) => Write("WARN", message);

    internal static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"backend-{DateTime.Now:yyyyMMdd}.log");
                using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Utf8NoBom.GetBytes($"{DateTime.Now:HH:mm:ss.fff} [{Pid}] [{level}] {message}{Environment.NewLine}");
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Elimina i log piu' vecchi di <paramref name="keepDays"/> giorni.</summary>
    internal static void Prune(int keepDays = 14)
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(LogDirectory, "backend-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-keepDays))
                    File.Delete(file);
            }
        }
        catch
        {
            // best effort
        }
    }
}
