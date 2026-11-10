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
        CancellationToken ct,
        string? standardInput = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // UTF-8 esplicito: svn --xml, svn diff e le CLI AI scrivono UTF-8; la codepage
            // OEM di default storpierebbe accenti nei path e nei messaggi di commit.
            StandardInputEncoding = standardInput is null ? null : new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList gestisce l'escaping di spazi/virgolette/caratteri speciali:
        // niente più concatenazione manuale di stringhe con quoting fragile.
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        // Se l'operazione viene annullata, il processo figlio va terminato:
        // altrimenti (es. svn commit) continuerebbe a girare da solo in background.
        using var killOnCancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* già terminato */ }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        if (standardInput is not null)
        {
            // stdin evita il limite di ~32K caratteri della riga di comando di Windows (es. diff lunghi).
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct);

        var output = await outputTask;
        var error = await errorTask;

        return new ProcessResult(process.ExitCode, output, error);
    }
}
