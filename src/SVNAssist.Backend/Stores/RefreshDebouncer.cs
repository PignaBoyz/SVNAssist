using System.IO;

namespace SVNAssist.Stores;

/// <summary>
/// Raggruppa una raffica di richieste di refresh (eventi del FileSystemWatcher) in un'unica
/// esecuzione, lanciata quando sono passati <c>quietPeriod</c> senza nuovi eventi.
/// </summary>
/// <remarks>
/// Sostituisce i tre timer separati di prima: un solo ciclo, che dorme finché non arriva
/// la prima richiesta. Gli eventi arrivati durante un refresh ne programmano un altro.
/// </remarks>
internal sealed class RefreshDebouncer
{
    private readonly TimeSpan _quietPeriod;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly SemaphoreSlim _wake = new(0);
    private long _lastRequestTicks;
    private int _pending;

    public RefreshDebouncer(TimeSpan quietPeriod, Func<CancellationToken, Task> refresh)
    {
        _quietPeriod = quietPeriod;
        _refresh = refresh;
    }

    /// <summary>Segnala che qualcosa è cambiato. Thread-safe e non bloccante.</summary>
    public void Request()
    {
        Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            _wake.Release();
    }

    /// <summary>Ciclo di servizio: termina quando <paramref name="ct"/> viene annullato.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(ct);

                // Attende che gli eventi si calmino: ogni nuovo evento sposta in avanti la scadenza.
                while (true)
                {
                    var last = new DateTime(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);
                    var remaining = _quietPeriod - (DateTime.UtcNow - last);
                    if (remaining <= TimeSpan.Zero)
                        break;

                    await Task.Delay(remaining, ct);
                }

                // Azzerato prima del refresh: gli eventi che arrivano mentre gira ne innescano un altro.
                Interlocked.Exchange(ref _pending, 0);

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
/// Decide quali eventi del file system ignorare: cartelle di build e metadati che cambiano di continuo
/// senza modificare lo stato SVN.
/// </summary>
internal static class WorkingCopyPathFilter
{
    private static readonly HashSet<string> NoiseFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svn", ".vs", "bin", "obj", "node_modules",
    };

    /// <summary><c>true</c> se l'evento su <paramref name="fullPath"/> va ignorato.</summary>
    /// <param name="workingCopyRoot">Root della working copy (solo i segmenti sotto la root contano).</param>
    /// <param name="fullPath">Percorso su cui è avvenuto l'evento.</param>
    public static bool IsNoise(string workingCopyRoot, string fullPath)
    {
        var relative = fullPath.StartsWith(workingCopyRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath[workingCopyRoot.Length..]
            : fullPath;

        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(NoiseFolders.Contains);
    }
}
