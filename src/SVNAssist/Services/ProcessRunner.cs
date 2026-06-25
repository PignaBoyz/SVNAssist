using System.Diagnostics;

namespace SVNAssist.Services;

/// <summary>
/// Implementazione di <see cref="IProcessRunner"/> basata su <see cref="Process"/>.
/// </summary>
/// <remarks>
/// Unica classe che fa lo spawn reale di un processo: isolando qui questa dipendenza,
/// il resto della logica (<see cref="SvnService"/>) diventa unit-testabile con un runner finto.
///
/// stdout e stderr vengono letti in parallelo per evitare deadlock: se leggessimo
/// un flusso alla volta, il buffer dell'altro potrebbe riempirsi e bloccare il processo figlio.
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList gestisce l'escaping di spazi/virgolette/caratteri speciali:
        // niente più concatenazione manuale di stringhe con quoting fragile.
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var output = await outputTask;
        var error = await errorTask;

        return new ProcessResult(process.ExitCode, output, error);
    }
}
