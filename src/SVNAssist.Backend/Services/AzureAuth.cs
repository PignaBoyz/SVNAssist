using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace SVNAssist.Services;

/// <summary>Come ci si autentica ad Azure DevOps: token PAT, account aziendale via Azure CLI o accesso Windows (SSO, Entra ID).</summary>
internal interface IAzureTokens
{
    /// <summary>Intestazione Authorization per le chiamate REST. Eccezione <see cref="AzureDevOpsException"/> se serve un accesso.</summary>
    Task<AuthenticationHeaderValue> HeaderAsync(AzureSettings s, CancellationToken ct);

    /// <summary>Accesso interattivo (solo SSO: finestra di Windows per l'account aziendale). Restituisce l'account.</summary>
    Task<string> SignInAsync(AzureSettings s, CancellationToken ct);
}

/// <summary>
/// Token per Azure DevOps (risorsa <see cref="Resource"/>):
/// <list type="bullet">
/// <item><c>pat</c>: Basic con il token personale.</item>
/// <item><c>cli</c>: <c>az account get-access-token</c> con l'account con cui l'utente ha fatto <c>az login</c>; nessuna registrazione app.</item>
/// <item><c>sso</c>: MSAL con il broker di Windows (WAM): account Windows aziendale, in silenzio; serve il client id di un'app Entra ID (dall'IT).</item>
/// </list>
/// I token Entra durano circa un'ora: in cache fino a 5 minuti prima della scadenza.
/// </summary>
internal sealed class AzureTokens(Func<Task<long>>? windowHandle = null) : IAzureTokens
{
    internal const string Resource = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly string[] Scopes = [Resource + "/.default"];
    private readonly object _lock = new();
    private (string Token, DateTimeOffset Expires)? _cli;
    private IPublicClientApplication? _pca;
    private string _pcaKey = string.Empty;
    private long _hwnd; // finestra di Visual Studio, padre della finestra di accesso di Windows

    public async Task<AuthenticationHeaderValue> HeaderAsync(AzureSettings s, CancellationToken ct) => s.Auth switch
    {
        "cli" => new AuthenticationHeaderValue("Bearer", await CliTokenAsync(ct)),
        "sso" => new AuthenticationHeaderValue("Bearer", await SsoTokenAsync(s, interactive: false, ct)),
        _ => new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + s.Pat))),
    };

    public async Task<string> SignInAsync(AzureSettings s, CancellationToken ct)
    {
        if (s.Auth != "sso")
            return string.Empty;
        await SsoTokenAsync(s, interactive: true, ct);
        var app = Pca(s);
        return (await app.GetAccountsAsync()).FirstOrDefault()?.Username ?? string.Empty;
    }

    // ── Azure CLI ───────────────────────────────────────────────────

    private async Task<string> CliTokenAsync(CancellationToken ct)
    {
        lock (_lock)
            if (_cli is { } c && c.Expires > DateTimeOffset.UtcNow.AddMinutes(5))
                return c.Token;

        var psi = new ProcessStartInfo("cmd.exe", $"/d /c az account get-access-token --resource {Resource} -o json")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        string output, error;
        int exit;
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var o = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var e = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            (output, error, exit) = (await o, await e, p.ExitCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new AzureDevOpsException(null, Loc.T("Azure CLI non avviabile: {0}", ex.Message));
        }

        if (exit != 0)
            throw new AzureDevOpsException(HttpStatusCode.Unauthorized, CliError(error));
        var (token, expires) = ParseCliToken(output);
        lock (_lock)
            _cli = (token, expires);
        return token;
    }

    /// <summary>Messaggio comprensibile da stderr di <c>az</c>: non installato, accesso da fare, altro.</summary>
    internal static string CliError(string stderr)
    {
        if (stderr.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) || stderr.Contains("non è riconosciuto", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Azure CLI non installato: usa \"Installa Azure CLI\" nella procedura guidata.");
        if (stderr.Contains("az login", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Accesso Azure CLI da fare o scaduto: usa \"Accedi (az login)\" nella procedura guidata.");
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return Loc.T("Azure CLI: {0}", line);
    }

    /// <summary>Token e scadenza dall'output JSON di <c>az account get-access-token</c> (<c>expires_on</c> in secondi, o <c>expiresOn</c> ora locale).</summary>
    internal static (string Token, DateTimeOffset Expires) ParseCliToken(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new AzureDevOpsException(null, Loc.T("Azure CLI: risposta non valida."));
        var token = node["accessToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
            throw new AzureDevOpsException(null, Loc.T("Azure CLI: risposta non valida."));
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(30);
        if (node["expires_on"] is { } e && long.TryParse(e.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            expires = DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (node["expiresOn"]?.GetValue<string>() is { } local && DateTime.TryParse(local, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            expires = new DateTimeOffset(d);
        return (token, expires);
    }

    // ── Accesso Windows (MSAL + WAM) ───────────────────────────────

    private IPublicClientApplication Pca(AzureSettings s)
    {
        var tenant = string.IsNullOrWhiteSpace(s.Tenant) ? "organizations" : s.Tenant.Trim();
        var key = s.ClientId + "|" + tenant;
        lock (_lock)
        {
            if (_pca is not null && _pcaKey == key)
                return _pca;
            _pca = PublicClientApplicationBuilder.Create(s.ClientId.Trim())
                .WithAuthority("https://login.microsoftonline.com/" + tenant)
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "SVNAssist" })
                .WithParentActivityOrWindow(() => new IntPtr(Interlocked.Read(ref _hwnd)))
                .Build();
            _pcaKey = key;
            return _pca;
        }
    }

    private async Task<string> SsoTokenAsync(AzureSettings s, bool interactive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.ClientId))
            throw new AzureDevOpsException(HttpStatusCode.Unauthorized, Loc.T("Accesso Windows: manca il client id dell'app Entra ID (te lo da' l'IT)."));
        if (windowHandle is not null)
        {
            try { Interlocked.Exchange(ref _hwnd, await windowHandle()); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { BackendLog.Info("Azure SSO: finestra di VS non nota: " + ex.Message); }
        }

        var app = Pca(s);
        var account = (await app.GetAccountsAsync()).FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
        try
        {
            return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalUiRequiredException) when (interactive)
        {
            try { return (await app.AcquireTokenInteractive(Scopes).ExecuteAsync(ct)).AccessToken; }
            catch (MsalException ex) { throw new AzureDevOpsException(HttpStatusCode.Unauthorized, Loc.T("Accesso Windows non riuscito: {0}", ex.Message)); }
        }
        catch (MsalUiRequiredException)
        {
            throw new AzureDevOpsException(HttpStatusCode.Unauthorized, Loc.T("Accesso aziendale richiesto: apri Collega Azure DevOps e premi Accedi."));
        }
        catch (MsalException ex)
        {
            throw new AzureDevOpsException(HttpStatusCode.Unauthorized, Loc.T("Accesso Windows non riuscito: {0}", ex.Message));
        }
    }
}

/// <summary>Solo PAT (test e default): Basic con il token personale.</summary>
internal sealed class PatTokens : IAzureTokens
{
    public Task<AuthenticationHeaderValue> HeaderAsync(AzureSettings s, CancellationToken ct)
        => Task.FromResult(new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + s.Pat))));

    public Task<string> SignInAsync(AzureSettings s, CancellationToken ct) => Task.FromResult(string.Empty);
}
