using System.Text;
using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Anteprima di "SVN Update": le revisioni in arrivo (<c>svn log</c> da BASE+1 a HEAD, con i file toccati) e, se un provider AI e' collegato,
/// un riassunto a parole semplici. Il riassunto e' facoltativo: se manca o fallisce l'elenco si vede lo stesso e l'Update funziona.
/// All'AI vanno solo autore, messaggi di log e nomi dei file, mai il contenuto dei file. L'update lo esegue <see cref="ChangesStore"/>.
/// </summary>
internal sealed class IncomingStore
{
    internal const int MaxShownRevisions = 50;
    private const int MaxFilesShown = 8;
    private const int MaxFilesToAi = 30;
    private const int MaxMessageChars = 400;

    /// <summary>Messaggio utente massimo verso l'AI: il limite piu' stretto dei provider (GitHub Models, debito D16).</summary>
    private static readonly int MaxAiChars = AiLimits.GitHubModels.MaxUserChars - 4_000;

    private readonly ISvnService _svn;
    private readonly IAiServiceFactory _aiFactory;
    private readonly object _lock = new();
    private IncomingDto? _state;
    private CancellationTokenSource? _aiCts;

    public IncomingStore(ISvnService svn, IAiServiceFactory aiFactory)
        => (_svn, _aiFactory) = (svn, aiFactory);

    public event Action? Changed;

    /// <summary>Provider che ha fallito il riassunto (per "Rifai l'accesso" / "Aggiorna elenco modelli").</summary>
    internal string? FailedProvider { get; private set; }

    internal IncomingDto? GetSnapshot() { lock (_lock) return _state; }

    /// <summary>Riassunto AI terminato (riuscito o no): serve ai test per aspettare il lavoro in background.</summary>
    internal Task? AiTask { get; private set; }

    /// <summary>
    /// Legge le revisioni in arrivo. <c>false</c> = niente da mostrare (nessuna revisione nuova, oppure server non raggiungibile:
    /// in quel caso l'Update parte come sempre e svn dara' l'errore vero): il chiamante fa l'update direttamente.
    /// <c>true</c> = il dialogo e' aperto e il riassunto AI e' partito in background.
    /// </summary>
    internal async Task<bool> OpenAsync(string branchUrl, long wcRevision, string branchLabel, CancellationToken ct)
    {
        IReadOnlyList<SvnLogEntry> log;
        try
        {
            var head = await _svn.GetHeadRevisionAsync(branchUrl, ct);
            if (head <= wcRevision)
                return false;
            // shortcut: tutto l'intervallo in un colpo (nessun limite -l), basta per working copy indietro di qualche decina di revisioni.
            log = await _svn.GetLogRangeAsync(branchUrl, wcRevision + 1, head, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Anteprima Update non disponibile: " + ex.Message);
            return false;
        }

        if (log.Count == 0)
            return false;

        var items = log.Take(MaxShownRevisions).Select(e => new IncomingRevDto(
            e.Revision, e.Author, e.Date.ToString("o"), Clip(e.Message, MaxMessageChars),
            (e.Paths ?? []).Take(MaxFilesShown).Select(p => new IncomingFileDto(p.Action, p.Path)).ToList(),
            Math.Max(0, (e.Paths?.Count ?? 0) - MaxFilesShown))).ToList();
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _aiCts?.Cancel();
            _aiCts = cts;
            FailedProvider = null;
            _state = new IncomingDto(branchLabel, log.Count, items, Math.Max(0, log.Count - items.Count), new IncomingAiDto("loading", string.Empty, string.Empty, null));
        }

        Changed?.Invoke();
        AiTask = SummarizeAsync(log, cts.Token); // in background: l'elenco e i pulsanti restano subito utilizzabili
        return true;
    }

    internal void Close()
    {
        lock (_lock)
        {
            _aiCts?.Cancel();
            _aiCts = null;
            _state = null;
        }

        Changed?.Invoke();
    }

    private async Task SummarizeAsync(IReadOnlyList<SvnLogEntry> log, CancellationToken ct)
    {
        IncomingAiDto result;
        try
        {
            var ai = await _aiFactory.CreateAsync(ct);
            if (ai is null)
                result = new IncomingAiDto("none", string.Empty, string.Empty, null); // AI non collegata: solo l'elenco
            else
            {
                var text = await ai.SummarizeIncomingAsync(BuildPrompt(log), Loc.Lang == "en" ? "english" : "italiano", ct);
                var last = ai is FallbackAiService fl ? fl.LastService : ai;
                // Sempre la riga di consumo (con il flag acceso): modello dei commit + token/costo se il provider li riporta.
                var usage = last is IReportsModel { LastUsage: { } u } ? AiUsageText.Format(u) : last is IReportsModel { LastModelUsed: { } m } ? AiUsageText.ShortModel(m) : string.Empty;
                result = new IncomingAiDto("done", text, usage, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = ErrorResult(ex.Message);
        }

        lock (_lock)
        {
            if (ct.IsCancellationRequested || _state is null)
                return;
            _state = _state with { Ai = result };
        }

        Changed?.Invoke();
    }

    private IncomingAiDto ErrorResult(string detail)
    {
        var kind = AiErrors.Classify(detail);
        FailedProvider = AiErrors.ProviderOf(detail);
        return kind switch
        {
            AiErrorKind.Auth => new IncomingAiDto("error", Loc.T("Riassunto AI non disponibile: accesso al provider scaduto o non valido.") + "\n" + detail, string.Empty,
                new StatusActionDto(Loc.T("Rifai l'accesso"), "incomingFixAuth")),
            AiErrorKind.ModelUnavailable or AiErrorKind.MaybeModel => new IncomingAiDto("error", Loc.T("Riassunto AI non disponibile: il modello scelto non risponde.") + "\n" + detail, string.Empty,
                new StatusActionDto(Loc.T("Aggiorna elenco modelli"), "incomingFixModels")),
            _ => new IncomingAiDto("error", Loc.T("Riassunto AI non disponibile: {0}", detail), string.Empty, null),
        };
    }

    /// <summary>Solo autore, messaggio e nomi dei file (mai contenuto), entro il limite del provider piu' stretto.</summary>
    internal static string BuildPrompt(IReadOnlyList<SvnLogEntry> log)
    {
        var sb = new StringBuilder();
        foreach (var e in log.OrderBy(x => x.Revision))
        {
            var block = new StringBuilder();
            block.Append('r').Append(e.Revision).Append(' ').Append(e.Author).Append(": ").AppendLine(Clip(e.Message.Replace('\n', ' ').Replace('\r', ' '), MaxMessageChars));
            var paths = e.Paths ?? [];
            foreach (var p in paths.Take(MaxFilesToAi))
                block.Append("  ").Append(p.Action).Append(' ').AppendLine(p.Path);
            if (paths.Count > MaxFilesToAi)
                block.Append("  ... +").Append(paths.Count - MaxFilesToAi).AppendLine(" file");
            if (sb.Length + block.Length > MaxAiChars)
            {
                sb.Append("... altre revisioni omesse per lunghezza");
                break;
            }

            sb.Append(block);
        }

        return sb.ToString();
    }

    private static string Clip(string s, int max)
    {
        s = (s ?? string.Empty).Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
