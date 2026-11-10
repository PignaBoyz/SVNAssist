using System.IO;

namespace SVNAssist.Services;

/// <summary>
/// Provider AI che usa <b>Claude Code</b> (<c>claude -p</c>) già installato e loggato:
/// sfrutta l'abbonamento Claude dell'utente, senza API key.
/// </summary>
/// <remarks>
/// Il prompt passa su stdin (i diff superano facilmente il limite della riga di comando).
/// Il processo gira senza strumenti, senza MCP e senza salvare la sessione: deve solo
/// rispondere con il testo del commit, mai toccare file.
/// </remarks>
internal sealed class ClaudeCodeAiService : AiServiceBase
{
    private readonly IProcessRunner _processRunner;
    private readonly string _claudeExePath;
    private readonly string _model;

    public ClaudeCodeAiService(IProcessRunner processRunner, string claudeExePath, string model)
    {
        _processRunner = processRunner;
        _claudeExePath = claudeExePath;
        _model = model;
    }

    /// <inheritdoc />
    protected override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        List<string> args =
        [
            "-p",
            "--output-format", "text",
            "--system-prompt", systemPrompt,
            "--tools", "",
            "--strict-mcp-config",
            "--no-session-persistence",
        ];
        if (!string.IsNullOrWhiteSpace(_model))
            args.AddRange(["--model", _model]);

        // Cartella temporanea come directory di lavoro: niente CLAUDE.md o impostazioni di progetto.
        var result = await _processRunner.RunAsync(
            _claudeExePath, args, Path.GetTempPath(), ct, standardInput: userMessage);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Claude Code fallito (exit code {result.ExitCode}): {FirstNonEmpty(result.StandardError, result.StandardOutput)}");

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? throw new InvalidOperationException("Claude Code non ha restituito testo.")
            : result.StandardOutput;
    }

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b.Trim() : a.Trim();

    /// <summary>Cerca <c>claude.exe</c> nel PATH e nel percorso d'installazione standard.</summary>
    public static string? FindClaudeExecutable()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "claude.exe"))
            .Append(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }
}
