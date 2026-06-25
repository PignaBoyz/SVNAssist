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
    /// <param name="ct">Token per annullare l'operazione.</param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct);
}
