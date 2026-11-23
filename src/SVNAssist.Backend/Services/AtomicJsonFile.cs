using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SVNAssist.Services;

/// <summary>
/// Lettura e scrittura atomica (temp univoco + File.Move + retry + mutex cross-processo per istanze VS multiple)
/// per i file JSON condivisi in %LOCALAPPDATA%\SVNAssist\.
/// </summary>
internal static class AtomicJsonFile
{
    private const int Retries = 4;

    /// <summary>Legge il file JSON con condivisione R/W; <c>null</c> se non esiste o se resta bloccato.</summary>
    internal static string? TryReadAllText(string path)
    {
        for (var i = 0; i < Retries; i++)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < Retries - 1)
            {
                Thread.Sleep(25 * (i + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Scrive in modo atomico usando un file temporaneo univoco per PID/GUID e <c>File.Move(..., overwrite: true)</c>.</summary>
    internal static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < Retries - 1)
                {
                    Thread.Sleep(25 * (i + 1));
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Esegue una lettura-modifica-scrittura serializzata fra piu' processi del backend sul medesimo file
    /// tramite un Mutex nominato per sessione Windows.
    /// </summary>
    internal static void WithFileLock(string path, Action action)
    {
        var mutexName = @"Local\SVNAssist-" + HashKey(path);
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            mutex = new Mutex(false, mutexName);
            try { acquired = mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { acquired = true; }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Ambienti ristretti senza permessi sui mutex nominati: si prosegue best effort con atomic move + retry.
        }

        try
        {
            action();
        }
        finally
        {
            if (acquired && mutex is not null)
            {
                try { mutex.ReleaseMutex(); }
                catch { /* best effort */ }
            }

            mutex?.Dispose();
        }
    }

    private static string HashKey(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(bytes)[..16];
    }
}
