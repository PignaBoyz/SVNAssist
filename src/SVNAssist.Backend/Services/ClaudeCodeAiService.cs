using System.IO;
using System.Text.Json;

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
internal sealed class ClaudeCodeAiService : AiServiceBase, IReportsModel
{
    private readonly IProcessRunner _processRunner;
    private readonly string _claudeExePath;
    private readonly string _model;

    /// <summary>Modello configurato (vuoto = il predefinito del provider, non noto): la UI lo scrive accanto al messaggio generato.</summary>
    public string? LastModelUsed => string.IsNullOrWhiteSpace(_model) ? null : _model;

    public ClaudeCodeAiService(IProcessRunner processRunner, string claudeExePath, string model)
    {
        _processRunner = processRunner;
        _claudeExePath = claudeExePath;
        _model = model;
    }

    /// <inheritdoc />
    public override AiLimits Limits => AiLimits.ClaudeCode;

    /// <inheritdoc />
    protected override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        List<string> args =
        [
            "-p",
            "--output-format", "json", // il JSON e' solo la sorgente di modello, token e costo: all'utente arriva il testo
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

        var parsed = ParseJsonResult(result.StandardOutput);
        // Con il JSON il testo dell'errore sta in "result" (is_error) o in "subtype", non piu' in stderr: lo stesso formato di prima
        // permette ad AiErrors.Classify di riconoscere accesso scaduto e modello ritirato.
        if (parsed is { IsError: true } || result.ExitCode != 0)
            throw new InvalidOperationException(
                Loc.T("Claude Code fallito (exit code {0}): {1}", result.ExitCode,
                    parsed?.ErrorText ?? FirstNonEmpty(result.StandardError, result.StandardOutput)));

        // Nessun JSON riconoscibile (CLI diversa da quella attesa): lo stdout e' il testo, senza consumo.
        var text = parsed is null ? result.StandardOutput : parsed.Text;
        if (parsed?.Usage is { } usage)
            AddUsage(usage with { Model = usage.Model ?? (string.IsNullOrWhiteSpace(_model) ? null : _model) });
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException(Loc.T("Claude Code non ha restituito testo."))
            : text;
    }

    internal sealed record CliResult(bool IsError, string Text, string? ErrorText, AiUsage? Usage);

    /// <summary>
    /// Legge l'output <c>--output-format json</c> di <c>claude -p</c>: un oggetto, o (versioni recenti) un array di eventi
    /// il cui ultimo <c>type=result</c> e' quello che conta. <c>null</c> se non e' JSON di quella forma.
    /// </summary>
    internal static CliResult? ParseJsonResult(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                JsonElement? last = null;
                foreach (var e in root.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "result")
                        last = e;
                if (last is null)
                    return null;
                root = last.Value;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var hasResult = root.TryGetProperty("result", out var res);
            if (!hasResult && !root.TryGetProperty("is_error", out _))
                return null;

            var text = hasResult && res.ValueKind == JsonValueKind.String ? res.GetString() ?? "" : "";
            var subtype = root.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True
                          || subtype is not null && subtype.StartsWith("error", StringComparison.OrdinalIgnoreCase);
            var errorText = string.Join(" - ", new[] { text.Trim(), subtype }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new CliResult(isError, text, errorText.Length > 0 ? errorText : null, ReadUsage(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AiUsage? ReadUsage(JsonElement root)
    {
        long inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            // La cache e' input a tutti gli effetti: si contano anche i token scritti/letti in cache.
            foreach (var n in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
                inTok += u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            outTok = u.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0;
        }

        // Piu' modelli nella stessa sessione: si mostra quello che ha prodotto piu' output.
        string? model = null;
        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
        {
            long best = -1;
            foreach (var m in mu.EnumerateObject())
            {
                var o = m.Value.ValueKind == JsonValueKind.Object && m.Value.TryGetProperty("outputTokens", out var ot) && ot.ValueKind == JsonValueKind.Number ? ot.GetInt64() : 0;
                if (o > best)
                {
                    best = o;
                    model = m.Name;
                }
            }
        }

        decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
        return inTok == 0 && outTok == 0 && cost is null && model is null ? null : new AiUsage(model, inTok, outTok, cost);
    }

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b.Trim() : a.Trim();

    /// <summary>
    /// Cerca Claude Code: <c>claude.exe</c> nel PATH e in <c>~/.local/bin</c> (installer nativo) oppure l'installazione
    /// npm (<c>claude.cmd</c>), risolta sul programma vero (vedi <see cref="ResolveNpmShim"/>).
    /// </summary>
    public static string? FindClaudeExecutable()
    {
        string? Find() => FindClaudeExecutable(
            SvnLocator.RootedPathDirectories(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Find() ?? (SVNAssist.Rpc.PathRefresh.Refresh() ? Find() : null); // PATH di VS vecchio: rileggilo dal registro
    }

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
