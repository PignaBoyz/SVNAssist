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
internal sealed class CopilotAiService : AiServiceBase
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
    protected override int MaxUserMessageChars => CopilotUserMessageChars;

    // Prompt troppo grandi (decine di file) fanno rispondere Copilot con "No response was returned": budget piu' prudente di LargeContext.
    internal const int CopilotUserMessageChars = 60_000;

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
        try
        {
            var first = await core(systemPrompt, userMessage, ct);
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // si riprova sotto
        }

        try
        {
            var second = await core(systemPrompt, TrimToMaxChars(userMessage, Math.Max(2000, userMessage.Length / 2)), ct);
            if (!string.IsNullOrWhiteSpace(second))
                return second;
            throw new InvalidOperationException(Loc.T("Copilot non ha risposto: riprova o scegli un modello diverso."));
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
        var client = await GetClientAsync(ct);

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

        return reply?.Data?.Content
            ?? throw new InvalidOperationException(Loc.T("Copilot non ha restituito alcun messaggio."));
    }

    private async Task<CopilotClient> GetClientAsync(CancellationToken ct)
    {
        await ClientLock.WaitAsync(ct);
        try
        {
            if (_sharedClient is not null && _sharedClientToken == _gitHubToken)
                return _sharedClient;

            if (_sharedClient is not null)
                await _sharedClient.DisposeAsync();
            _sharedClient = null;

            var runtimePath = await CopilotRuntimeInstaller.EnsureInstalledAsync(_httpClient, ct);
            var client = new CopilotClient(new CopilotClientOptions
            {
                GitHubToken = _gitHubToken,
                Connection = RuntimeConnection.ForStdio(runtimePath),
            });
            await client.StartAsync(ct);
            _sharedClient = client;
            _sharedClientToken = _gitHubToken;
            return client;
        }
        finally
        {
            ClientLock.Release();
        }
    }
}
