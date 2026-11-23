using System.IO;

namespace SVNAssist.Stores;

/// <summary>
/// Raggruppa una raffica di richieste di refresh (eventi del FileSystemWatcher) in un'unica
/// esecuzione, lanciata quando sono passati <c>quietPeriod</c> senza nuovi eventi (e quando non in pausa).
/// </summary>
/// <remarks>
/// Sostituisce i tre timer separati di prima: un solo ciclo, che dorme finché non arriva
/// la prima richiesta. Gli eventi arrivati durante un refresh ne programmano un altro.
/// </remarks>
internal sealed class RefreshDebouncer
{
    private readonly TimeSpan _quietPeriod;
    private readonly Func<TimeSpan>? _getQuietPeriod;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private long _lastRequestTicks;
    private bool _pending;
    private bool _wakePending;
    private bool _paused;

    public RefreshDebouncer(TimeSpan quietPeriod, Func<CancellationToken, Task> refresh, Func<TimeSpan>? getQuietPeriod = null)
    {
        _quietPeriod = quietPeriod;
        _refresh = refresh;
        _getQuietPeriod = getQuietPeriod;
    }

    /// <summary>Segnala che qualcosa è cambiato. Thread-safe e non bloccante.</summary>
    public void Request()
    {
        lock (_gate)
        {
            _lastRequestTicks = DateTime.UtcNow.Ticks;
            _pending = true;
            if (!_paused && !_wakePending)
            {
                _wakePending = true;
                _wake.Release();
            }
        }
    }

    /// <summary>
    /// Mette in pausa (<c>true</c>) o riprende (<c>false</c>) i refresh automatici (es. build o caricamento solution).
    /// Gli eventi accumulati in pausa si consolidano in un solo refresh alla ripresa.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
                return;
            _paused = paused;
            if (!paused && _pending && !_wakePending)
            {
                _lastRequestTicks = DateTime.UtcNow.Ticks;
                _wakePending = true;
                _wake.Release();
            }
        }
    }

    /// <summary>Ciclo di servizio: termina quando <paramref name="ct"/> viene annullato.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(ct);

                var shouldRun = false;
                while (true)
                {
                    TimeSpan remaining;
                    lock (_gate)
                    {
                        if (_paused)
                        {
                            _wakePending = false;
                            shouldRun = false;
                            break;
                        }

                        var quiet = _getQuietPeriod?.Invoke() ?? _quietPeriod;
                        var last = new DateTime(_lastRequestTicks, DateTimeKind.Utc);
                        remaining = quiet - (DateTime.UtcNow - last);
                        if (remaining <= TimeSpan.Zero)
                        {
                            // Azzerato prima del refresh: gli eventi che arrivano mentre gira ne innescano un altro.
                            _pending = false;
                            _wakePending = false;
                            shouldRun = true;
                            break;
                        }
                    }

                    await Task.Delay(remaining, ct);
                }

                if (!shouldRun)
                    continue;

                try
                {
                    await _refresh(ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Un refresh fallito (svn occupato, cartella sparita) non deve fermare il ciclo.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normale alla chiusura della tool window.
        }
    }
}

/// <summary>
/// Decide quali eventi del file system ignorare: cartelle di build e file temporanei/metadati che cambiano di continuo
/// senza modificare lo stato SVN.
/// </summary>
internal static class WorkingCopyPathFilter
{
    private static readonly HashSet<string> NoiseFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svn", ".vs", ".svn-assist", ".git", "bin", "obj", "node_modules",
        "packages", "TestResults", "artifacts", "Debug", "Release", "x64", "x86", ".idea", ".vscode",
    };

    private static readonly HashSet<string> NoiseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".user", ".suo", ".cache", ".db-wal", ".db-shm", ".log",
        ".pdb", ".ilk", ".tlog", ".lastbuildstate", ".up2date",
    };

    /// <summary><c>true</c> se l'evento su <paramref name="fullPath"/> va ignorato.</summary>
    /// <param name="workingCopyRoot">Root della working copy (solo i segmenti sotto la root contano).</param>
    /// <param name="fullPath">Percorso su cui è avvenuto l'evento.</param>
    public static bool IsNoise(string workingCopyRoot, string fullPath)
    {
        var relative = fullPath.StartsWith(workingCopyRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath[workingCopyRoot.Length..]
            : fullPath;

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(NoiseFolders.Contains))
            return true;

        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;
        if (fileName.Length == 0)
            return false;
        if (fileName.StartsWith("~$", StringComparison.Ordinal) || fileName.EndsWith("~", StringComparison.Ordinal))
            return true;
        if (fileName.EndsWith(".FileListAbsolute.txt", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && NoiseExtensions.Contains(ext);
    }
}
