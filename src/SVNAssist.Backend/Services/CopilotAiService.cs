using System.Net.Http;
using GitHub.Copilot;

namespace SVNAssist.Services;

/// <summary>
/// Provider AI che usa l'abbonamento <b>GitHub Copilot</b> dell'utente tramite
/// <c>GitHub.Copilot.SDK</c> (il runtime Copilot viene scaricato al primo uso da <see cref="CopilotRuntimeInstaller"/>).
/// </summary>
/// <remarks>
/// È la cosa più vicina al "commit message generato da Copilot" di Git Changes: l'SDK delle
/// estensioni VS non espone l'AI di Copilot, ma questo SDK sì, con lo stesso account GitHub.
///
/// Il client (un processo runtime) è costoso da avviare: è condiviso e riusato tra le
/// richieste. La sessione invece è usa-e-getta, senza strumenti: deve solo scrivere testo.
/// Ogni richiesta può consumare "premium requests" in base al modello scelto.
/// </remarks>
internal sealed class CopilotAiService : AiServiceBase, IReportsModel
{
    private static readonly SemaphoreSlim ClientLock = new(1, 1);
    private static CopilotClient? _sharedClient;
    private static string? _sharedClientToken;

    private readonly HttpClient _httpClient;
    private readonly string? _gitHubToken;
    private readonly string _model;

    /// <param name="gitHubToken">Token GitHub; se null usa il login della Copilot CLI.</param>
    /// <param name="model">Modello Copilot; vuoto = default di Copilot.</param>
    public CopilotAiService(HttpClient httpClient, string? gitHubToken, string model)
    {
        _httpClient = httpClient;
        _gitHubToken = string.IsNullOrWhiteSpace(gitHubToken) ? null : gitHubToken;
        _model = model;
    }

    /// <inheritdoc />
    public override AiLimits Limits => AiLimits.Copilot;

    /// <summary>Modello che Copilot ha davvero usato per l'ultima risposta (<c>AssistantMessageData.Model</c>); null se non lo ha detto.</summary>
    public string? LastModelUsed { get; private set; }

    /// <summary>Solo test: sostituisce la chiamata reale al runtime Copilot.</summary>
    internal Func<string, string, CancellationToken, Task<string>>? CoreOverride { get; init; }

    /// <inheritdoc />
    protected override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        try
        {
            return await CompleteWithRetryAsync(systemPrompt, userMessage, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Runtime morto o sessione rotta: si butta il client condiviso, la prossima chiamata lo riavvia.
            await DropSharedClientAsync();
            throw;
        }
    }

    // Risposta vuota / "No response was returned": un solo nuovo tentativo con il messaggio dimezzato.
    private async Task<string> CompleteWithRetryAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var core = CoreOverride ?? CompleteCoreAsync;
        Exception? firstError = null;
        try
        {
            var first = await core(systemPrompt, userMessage, ct);
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Modello ritirato/non disponibile: riprovare con lo stesso modello non serve, l'errore vero va mostrato subito.
            if (AiErrors.Classify(ex.Message) == AiErrorKind.ModelUnavailable)
                throw;
            firstError = ex; // si riprova sotto
        }

        try
        {
            var second = await core(systemPrompt, TrimToMaxChars(userMessage, Math.Max(2000, userMessage.Length / 2)), ct);
            if (!string.IsNullOrWhiteSpace(second))
                return second;
            throw new InvalidOperationException(Loc.T("Copilot non ha risposto: riprova o scegli un modello diverso.")
                + (firstError is null ? string.Empty : " (" + firstError.Message + ")"));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not InvalidOperationException { InnerException: null })
        {
            throw new InvalidOperationException(Loc.T("Copilot non ha risposto: riprova o scegli un modello diverso.") + " (" + ex.Message + ")", ex);
        }
    }

    private static async Task DropSharedClientAsync()
    {
        await ClientLock.WaitAsync();
        try
        {
            var client = _sharedClient;
            _sharedClient = null;
            _sharedClientToken = null;
            if (client is not null)
            {
                try { await client.DisposeAsync(); }
                catch (Exception) { /* il runtime potrebbe essere già morto */ }
            }
        }
        finally
        {
            ClientLock.Release();
        }
    }

    private async Task<string> CompleteCoreAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var client = await GetSharedClientAsync(_httpClient, _gitHubToken, ct);

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = string.IsNullOrWhiteSpace(_model) ? null : _model,
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = systemPrompt },
            // Nessuno strumento: il modello deve solo restituire testo, mai leggere/scrivere file.
            AvailableTools = [],
            // shortcut: PermissionDecision è marcata sperimentale (GHCP001); se cambia nome
            // nelle prossime versioni dell'SDK, aggiornare qui. Rifiutiamo sempre.
#pragma warning disable GHCP001
            OnPermissionRequest = (_, _) => Task.FromResult(
                GitHub.Copilot.Rpc.PermissionDecision.Reject("SVNAssist: nessuno strumento consentito")),
#pragma warning restore GHCP001
        });

        var reply = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = userMessage }, TimeSpan.FromMinutes(2), ct);

        var content = reply?.Data?.Content;
        if (content is not null && !string.IsNullOrWhiteSpace(reply?.Data?.Model))
            LastModelUsed = reply!.Data!.Model;
        return content
            ?? throw new InvalidOperationException(Loc.T("Copilot non ha restituito alcun messaggio."));
    }

    /// <summary>
    /// Modelli e account di Copilot dal runtime (<c>ListModelsAsync</c>, <c>GetAuthStatusAsync</c>). L'SDK tiene l'elenco in cache finche' il client
    /// resta connesso: con <paramref name="force"/> si riavvia il client, cosi' "Aggiorna elenco" interroga davvero Copilot.
    /// </summary>
    internal static async Task<(IReadOnlyList<(string Id, string? Name, string? State, double? Multiplier)> Models, string? Account, string? DefaultModelId)> ListModelsAsync(
        HttpClient httpClient, string? token, bool force, CancellationToken ct)
    {
        if (force)
            await DropSharedClientAsync();
        var client = await GetSharedClientAsync(httpClient, string.IsNullOrWhiteSpace(token) ? null : token, ct);

        string? account = null;
        try
        {
            var auth = await client.GetAuthStatusAsync(ct);
            account = auth.IsAuthenticated
                ? Loc.T("Account GitHub: {0} (accesso: {1})", string.IsNullOrWhiteSpace(auth.Login) ? "?" : auth.Login, string.IsNullOrWhiteSpace(auth.AuthType) ? "?" : auth.AuthType)
                : Loc.T("Copilot non è collegato: {0}", string.IsNullOrWhiteSpace(auth.StatusMessage) ? Loc.T("accedi a GitHub (gh auth login)") : auth.StatusMessage);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Stato di accesso Copilot non disponibile: " + ex.Message);
        }

        // Modello predefinito: quello che il runtime sceglie per una sessione senza modello esplicito (nessuna richiesta inviata, nessun costo).
        string? defaultModel = null;
        try
        {
            await using var probe = await client.CreateSessionAsync(new SessionConfig
            {
                AvailableTools = [],
#pragma warning disable GHCP001
                OnPermissionRequest = (_, _) => Task.FromResult(GitHub.Copilot.Rpc.PermissionDecision.Reject("SVNAssist: nessuno strumento consentito")),
#pragma warning restore GHCP001
            });
            defaultModel = (await probe.Rpc.Model.GetCurrentAsync(ct))?.ModelId;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            BackendLog.Warn("Modello predefinito di Copilot non rilevabile: " + ex.Message);
        }

        try
        {
            var models = await client.ListModelsAsync(ct);
            return (models.Select(m => (m.Id, (string?)m.Name, (string?)m.Policy?.State, (double?)m.Billing?.Multiplier)).ToList(), account, defaultModel);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && account is not null)
        {
            // L'account e' il perche' piu' probabile (non collegato, nessun posto Copilot, policy dell'organizzazione): non va perso con l'errore.
            throw new InvalidOperationException(account + " — " + ex.Message, ex);
        }
    }

    private static async Task<CopilotClient> GetSharedClientAsync(HttpClient httpClient, string? token, CancellationToken ct)
    {
        await ClientLock.WaitAsync(ct);
        try
        {
            if (_sharedClient is not null && _sharedClientToken == token)
                return _sharedClient;

            if (_sharedClient is not null)
                await _sharedClient.DisposeAsync();
            _sharedClient = null;

            var runtimePath = await CopilotRuntimeInstaller.EnsureInstalledAsync(httpClient, ct);
            var client = new CopilotClient(new CopilotClientOptions
            {
                GitHubToken = token,
                Connection = RuntimeConnection.ForStdio(runtimePath),
            });
            await client.StartAsync(ct);
            _sharedClient = client;
            _sharedClientToken = token;
            return client;
        }
        finally
        {
            ClientLock.Release();
        }
    }
}
