namespace SVNAssist.Services;

/// <summary>
/// Risultato dell'esecuzione di un processo esterno.
/// </summary>
/// <param name="ExitCode">Codice di uscita del processo (0 = successo).</param>
/// <param name="StandardOutput">Testo catturato da stdout.</param>
/// <param name="StandardError">Testo catturato da stderr.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Astrae l'esecuzione di un processo esterno (es. <c>svn.exe</c>).
/// </summary>
/// <remarks>
/// Perché un'interfaccia invece di chiamare <see cref="System.Diagnostics.Process"/> direttamente?
/// - Permette di <b>unit-testare</b> la costruzione degli argomenti dei comandi SVN
///   con un runner finto, senza bisogno che SVN sia installato sulla macchina di build/CI.
/// - Isola la parte non testabile (il process spawn reale) in un'unica classe
///   (<see cref="ProcessRunner"/>), mantenendo <see cref="SvnService"/> pura logica.
///
/// Gli argomenti sono passati come <see cref="IReadOnlyList{T}"/> e <b>non</b> come singola
/// stringa: così l'escaping (spazi, virgolette, caratteri speciali nei path o nei messaggi
/// di commit) è gestito dal runtime tramite <c>ProcessStartInfo.ArgumentList</c>,
/// evitando il fragile escaping manuale.
/// </remarks>
public interface IProcessRunner
{
    /// <summary>
    /// Esegue un processo e ne attende il completamento, catturando stdout/stderr.
    /// </summary>
    /// <param name="fileName">Percorso dell'eseguibile.</param>
    /// <param name="arguments">Argomenti (uno per elemento, senza escaping manuale).</param>
    /// <param name="workingDirectory">Directory di lavoro (null = corrente).</param>
    /// <param name="ct">Token per annullare l'operazione (il processo viene terminato).</param>
    /// <param name="standardInput">Testo da scrivere su stdin (null = nessun input).</param>
    /// <param name="environment">
    /// Variabili d'ambiente da aggiungere/sovrascrivere per il solo processo figlio
    /// (null = ambiente ereditato invariato). Usato per forzare l'output di svn in inglese.
    /// </param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null);
}

internal static class ProcessRunnerExtensions
{
    /// <summary>
    /// Come <see cref="IProcessRunner.RunAsync"/> ma con un tempo massimo: scaduto il processo viene terminato e si lancia
    /// <see cref="TimeoutException"/> (distinta dall'annullamento dell'utente, che resta <see cref="OperationCanceledException"/>).
    /// </summary>
    internal static async Task<ProcessResult> RunWithTimeoutAsync(
        this IProcessRunner runner,
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        string description,
        CancellationToken ct,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await runner.RunAsync(fileName, arguments, workingDirectory, timeoutCts.Token, standardInput, environment);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"{description} non ha risposto entro {timeout.TotalSeconds:0} secondi ed è stato interrotto.");
        }
    }
}
