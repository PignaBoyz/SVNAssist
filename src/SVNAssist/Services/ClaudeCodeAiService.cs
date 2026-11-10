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
    protected override int MaxUserMessageChars => LargeContextUserMessageChars;

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

        // Cartella vuota dedicata (non %TEMP%, dove potrebbero esserci un CLAUDE.md o impostazioni di altri progetti).
        var (fileName, prefix) = BuildLaunch(_claudeExePath);
        var result = await _processRunner.RunAsync(
            fileName, [.. prefix, .. args], EnsureEmptyWorkingDirectory(), ct, standardInput: userMessage);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Claude Code fallito (exit code {result.ExitCode}): {FirstNonEmpty(result.StandardError, result.StandardOutput)}");

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? throw new InvalidOperationException("Claude Code non ha restituito testo.")
            : result.StandardOutput;
    }

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b.Trim() : a.Trim();

    /// <summary>
    /// Cerca Claude Code: <c>claude.exe</c> nel PATH e in <c>~/.local/bin</c> (installer nativo) oppure l'installazione
    /// npm (<c>claude.cmd</c>), risolta sul programma vero (vedi <see cref="ResolveNpmShim"/>).
    /// </summary>
    public static string? FindClaudeExecutable() => FindClaudeExecutable(
        SvnLocator.RootedPathDirectories(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string? FindClaudeExecutable(IEnumerable<string> pathDirs, string userProfile)
    {
        var dirs = pathDirs.ToList();
        var exe = dirs.Select(dir => Path.Combine(dir, "claude.exe"))
            .Append(Path.Combine(userProfile, ".local", "bin", "claude.exe"))
            .FirstOrDefault(File.Exists);
        if (exe is not null)
            return exe;

        return dirs.Select(dir => Path.Combine(dir, "claude.cmd")).Where(File.Exists)
            .Select(ResolveNpmShim).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// Lo shim npm <c>claude.cmd</c> non si lancia direttamente: <c>cmd.exe</c> reinterpreta virgolette, <c>&amp;</c>, <c>%</c> e
    /// a capo del system prompt passato negli argomenti. Si punta quindi al programma che lo shim avvia:
    /// <c>node_modules\@anthropic-ai\claude-codein\claude.exe</c> (versioni native) o <c>cli.js</c> (da eseguire con node).
    /// </summary>
    internal static string? ResolveNpmShim(string cmdPath)
    {
        var package = Path.Combine(Path.GetDirectoryName(cmdPath)!, "node_modules", "@anthropic-ai", "claude-code");
        var native = Path.Combine(package, "bin", "claude.exe");
        if (File.Exists(native))
            return native;

        var script = Path.Combine(package, "cli.js");
        return File.Exists(script) ? script : null;
    }

    /// <summary><c>%LOCALAPPDATA%\SVNAssist\empty</c>: cartella vuota usata come directory di lavoro di <c>claude -p</c>.</summary>
    internal static string EnsureEmptyWorkingDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "empty");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Eseguibile e argomenti iniziali: un <c>cli.js</c> va lanciato con node (accanto allo shim o nel PATH).</summary>
    internal static (string FileName, IReadOnlyList<string> Prefix) BuildLaunch(string claudePath)
    {
        if (!claudePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return (claudePath, []);

        var sibling = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(claudePath)!, "..", "..", "..", "node.exe"));
        return (File.Exists(sibling) ? sibling : "node", [claudePath]);
    }
}
