using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SVNAssist.Services;

/// <summary>
/// Impostazioni dell'integrazione Azure DevOps (facoltativa, spenta di default): organizzazione (es. https://dev.azure.com/azienda),
/// progetto, token PAT personale e cosa fare dopo i commit. <see cref="Ready"/> = attiva e configurata: solo allora parte una chiamata.
/// </summary>
/// <param name="Auth">pat | cli (Azure CLI, account aziendale) | sso (accesso Windows, Entra ID: serve <paramref name="ClientId"/>).</param>
internal sealed record AzureSettings(bool Enabled, string Org, string Project, string Pat, bool LinkOnCommit = true, bool Comment = false,
    string Auth = "pat", string ClientId = "", string Tenant = "")
{
    internal static readonly AzureSettings Off = new(false, string.Empty, string.Empty, string.Empty);

    internal static readonly string[] AuthModes = ["pat", "cli", "sso"];

    /// <summary>Organizzazione, progetto e il necessario per il modo di accesso (token PAT; client id per l'accesso Windows; niente per Azure CLI).</summary>
    internal bool Configured => Org.Length > 0 && Project.Length > 0 && Auth switch
    {
        "cli" => true,
        "sso" => ClientId.Length > 0,
        _ => Pat.Length > 0,
    };

    internal bool Ready => Enabled && Configured;

    /// <summary>URL dell'organizzazione senza la barra finale; accetta anche "azienda" (diventa https://dev.azure.com/azienda).</summary>
    internal static string NormalizeOrg(string org)
    {
        org = (org ?? string.Empty).Trim().TrimEnd('/');
        if (org.Length == 0)
            return org;
        if (!org.Contains("://", StringComparison.Ordinal))
            org = org.Contains('.', StringComparison.Ordinal) ? "https://" + org : "https://dev.azure.com/" + org;
        return org;
    }
}

/// <summary>Uno stato di un tipo di work item, con la sua categoria (Proposed, InProgress, Resolved, Completed, Removed).</summary>
internal sealed record WorkItemState(string Name, string Category);

/// <summary>
/// Come arrivare a uno stato rispettando le regole del processo (es. niente Proposed → Closed): si passa per gli stati delle
/// categorie intermedie, nell'ordine standard di Azure (proposto → in corso → risolto → completato).
/// </summary>
internal static class StatePath
{
    private static readonly string[] Order = ["Proposed", "InProgress", "Resolved", "Completed"];

    private static int Rank(string category)
    {
        var i = Array.FindIndex(Order, o => string.Equals(o, category, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? -1 : i;
    }

    /// <summary>
    /// Stati da impostare in ordine (intermedi + destinazione) da <paramref name="current"/> a <paramref name="target"/>.
    /// Indietro, stessa categoria, Removed o categorie sconosciute: solo la destinazione. Vuoto se si e' gia' li'.
    /// </summary>
    internal static IReadOnlyList<string> Steps(IReadOnlyList<WorkItemState> states, string current, string target)
    {
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            return [];
        var cur = Rank(states.FirstOrDefault(s => string.Equals(s.Name, current, StringComparison.OrdinalIgnoreCase))?.Category ?? string.Empty);
        var tgt = Rank(states.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase))?.Category ?? string.Empty);
        var steps = new List<string>();
        if (cur >= 0 && tgt > cur + 1)
            for (int r = cur + 1; r < tgt; r++)
                if (states.FirstOrDefault(s => Rank(s.Category) == r) is { } mid)
                    steps.Add(mid.Name);
        steps.Add(target);
        return steps;
    }
}

/// <summary>Un work item: tipo (Bug, Task...), titolo, stato e link alla sua pagina in Azure DevOps.</summary>
internal sealed record WorkItemInfo(int Id, string Type, string Title, string State, string Url);

/// <summary>
/// Formato del messaggio di commit, leggibile anche senza estensione:
/// <code>
/// Bug 81018 - Titolo del work item
/// https://dev.azure.com/azienda/IDC/_workitems/edit/81018
///
/// descrizione...
/// </code>
/// L'estensione riconosce i work item dal link (<c>/_workitems/edit/N</c>): niente regex su formati scritti a mano.
/// </summary>
internal static class WorkItemText
{
    private static readonly Regex EditUrl = new(@"/_workitems/edit/(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Url(string org, string project, int id)
        => $"{AzureSettings.NormalizeOrg(org)}/{Uri.EscapeDataString(project)}/_workitems/edit/{id.ToString(CultureInfo.InvariantCulture)}";

    internal static string Header(WorkItemInfo w) => $"{w.Type} {w.Id.ToString(CultureInfo.InvariantCulture)} - {w.Title}\n{w.Url}";

    /// <summary>Work item collegati nel messaggio (dai link), in ordine e senza doppioni.</summary>
    internal static IReadOnlyList<int> IdsIn(string? message)
        => string.IsNullOrEmpty(message) ? [] : EditUrl.Matches(message).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToList();

    /// <summary>
    /// Aggiunge l'intestazione del work item in cima al messaggio, dopo quelle gia' presenti; se c'e' gia' lo lascia com'e'.
    /// </summary>
    internal static string WithHeader(string message, WorkItemInfo w)
    {
        message = (message ?? string.Empty).Replace("\r\n", "\n");
        if (IdsIn(message).Contains(w.Id))
            return message;
        var lines = message.Split('\n').ToList();
        int i = 0; // fine del blocco di intestazioni: coppie "Tipo N - titolo" + riga con il link
        while (i + 1 < lines.Count && EditUrl.IsMatch(lines[i + 1]) && !EditUrl.IsMatch(lines[i]))
            i += 2;
        var head = lines.Take(i).Append(Header(w));
        var body = string.Join("\n", lines.Skip(i)).Trim('\n');
        return string.Join("\n", head) + (body.Length > 0 ? "\n\n" + body : "\n");
    }

    /// <summary>Le intestazioni dei work item in cima al messaggio (coppie "Tipo N - titolo" + link), senza il resto.</summary>
    internal static string HeaderBlock(string message)
    {
        var lines = (message ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i + 1 < lines.Length && EditUrl.IsMatch(lines[i + 1]) && !EditUrl.IsMatch(lines[i]))
            i += 2;
        return string.Join("\n", lines.Take(i));
    }

    /// <summary>
    /// L'AI ha riscritto il messaggio (Genera / Migliora): le intestazioni dei work item restano in cima com'erano,
    /// anche se l'AI le ha tolte, spostate o riscritte. Senza intestazioni prima, il testo dell'AI resta com'e'.
    /// </summary>
    internal static string KeepHeaders(string before, string after)
    {
        var block = HeaderBlock(before);
        if (block.Length == 0)
            return after;
        var body = (after ?? string.Empty).Replace("\r\n", "\n");
        foreach (var id in IdsIn(block))
            body = WithoutHeader(body, id);
        var keep = IdsIn(block).Select(i => "/_workitems/edit/" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        body = string.Join("\n", body.Split('\n').Where(l => !keep.Any(k => l.Contains(k, StringComparison.Ordinal)))).Trim('\n');
        return block + (body.Length > 0 ? "\n\n" + body : "\n");
    }

    /// <summary>Toglie l'intestazione (le due righe) di un work item dal messaggio.</summary>
    internal static string WithoutHeader(string message, int id)
    {
        var lines = (message ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            var m = EditUrl.Match(lines[i + 1]);
            if (m.Success && m.Groups[1].Value == id.ToString(CultureInfo.InvariantCulture))
            {
                lines.RemoveRange(i, 2);
                break;
            }
        }

        return string.Join("\n", lines).TrimStart('\n');
    }

    /// <summary>
    /// "#81018" (o "#810" mentre si scrive) alla fine del messaggio, per la tendina: il testo dopo # e la sua posizione; null se non c'e'.
    /// </summary>
    internal static (string Query, int Start)? HashQuery(string message)
    {
        var m = Regex.Match(message ?? string.Empty, @"(?:^|\s)#(\d{1,9})$", RegexOptions.CultureInvariant);
        return m.Success ? (m.Groups[1].Value, m.Groups[1].Index - 1) : null;
    }

    /// <summary>Prima riga utile della descrizione (dopo le intestazioni dei work item), per il commento del link in Azure.</summary>
    internal static string Summary(string message)
    {
        var lines = (message ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i + 1 < lines.Length && EditUrl.IsMatch(lines[i + 1])) { i++; continue; }
            if (EditUrl.IsMatch(lines[i]) || lines[i].Trim().Length == 0) continue;
            var s = lines[i].Trim();
            return s.Length > 120 ? s[..117] + "..." : s;
        }

        return string.Empty;
    }
}

/// <summary>Errore di Azure DevOps con un messaggio comprensibile (token scaduto, permessi, non trovato).</summary>
internal sealed class AzureDevOpsException(HttpStatusCode? status, string message, string code = "") : Exception(message)
{
    /// <summary>Cosa sistemare (per l'aiuto della procedura guidata): cli-missing, cli-login, sso-clientid, sso-login, auth, forbidden, notfound o vuoto.</summary>
    public string Code { get; } = code.Length > 0 ? code
        : status == HttpStatusCode.Unauthorized ? "auth" : status == HttpStatusCode.Forbidden ? "forbidden" : status == HttpStatusCode.NotFound ? "notfound" : string.Empty;

    internal HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Chiamate REST ad Azure DevOps (api-version 7.1) con il token PAT dell'utente (Basic, utente vuoto).
/// Solo cio' che serve all'estensione: verifica, lettura work item, ricerca dei propri, link, stato, commento.
/// </summary>
internal sealed class AzureDevOpsClient(HttpClient http, IAzureTokens? tokens = null)
{
    private readonly IAzureTokens _tokens = tokens ?? new PatTokens();

    private const string Api = "api-version=7.1";
    private static readonly string[] Fields = ["System.Id", "System.WorkItemType", "System.Title", "System.State"];

    private static RequestSpec Request(HttpMethod method, string url, AzureSettings s, JsonNode? body = null, string mediaType = "application/json")
        => new(method, url, s, body, mediaType);

    /// <summary>Richiesta da fare: l'intestazione di accesso si aggiunge in <see cref="SendAsync"/> (puo' richiedere un token Entra).</summary>
    private sealed record RequestSpec(HttpMethod Method, string Url, AzureSettings S, JsonNode? Body, string MediaType);

    private async Task<JsonNode?> SendAsync(RequestSpec spec, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(spec.Method, spec.Url);
        request.Headers.Authorization = await _tokens.HeaderAsync(spec.S, ct);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (spec.Body is not null)
            request.Content = new StringContent(spec.Body.ToJsonString(), Encoding.UTF8, spec.MediaType);
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AzureDevOpsException(null, Loc.T("Azure DevOps non raggiungibile: {0}", ex.Message));
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            // Un token non valido spesso non da' 401 ma la pagina di accesso (HTML, 203): va trattato come token non valido.
            var html = resp.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation || (resp.IsSuccessStatusCode && html))
                throw new AzureDevOpsException(HttpStatusCode.Unauthorized, spec.S.Auth == "pat" ? Loc.T("Token PAT non valido o scaduto.") : Loc.T("Accesso non valido o scaduto per questa organizzazione."));
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new AzureDevOpsException(resp.StatusCode, spec.S.Auth == "pat" ? Loc.T("Il token PAT non ha il permesso necessario (Work Items: Read & write).") : Loc.T("Il tuo account non ha il permesso necessario su questo progetto."));
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new AzureDevOpsException(resp.StatusCode, Loc.T("Organizzazione, progetto o work item non trovato."));
            if (!resp.IsSuccessStatusCode)
                throw new AzureDevOpsException(resp.StatusCode, Loc.T("Azure DevOps ha risposto {0}: {1}", (int)resp.StatusCode, ErrorMessage(text)));
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    /// <summary>Il messaggio di errore di Azure (campo "message" del JSON), o il testo accorciato.</summary>
    internal static string ErrorMessage(string text)
    {
        try
        {
            if (JsonNode.Parse(text)?["message"]?.GetValue<string>() is { Length: > 0 } m)
                return m;
        }
        catch (JsonException) { }
        return text.Length > 200 ? text[..200] : text;
    }

    private static string Org(AzureSettings s) => AzureSettings.NormalizeOrg(s.Org);

    private static string Proj(AzureSettings s) => Org(s) + "/" + Uri.EscapeDataString(s.Project);

    /// <summary>Nome dell'utente del token (verifica organizzazione + token).</summary>
    public async Task<string> CheckUserAsync(AzureSettings s, CancellationToken ct)
    {
        var json = await SendAsync(Request(HttpMethod.Get, $"{Org(s)}/_apis/connectionData", s), ct);
        var user = json?["authenticatedUser"];
        var name = user?["providerDisplayName"]?.GetValue<string>() ?? user?["customDisplayName"]?.GetValue<string>();
        // Token sbagliato: risponde come utente anonimo invece che con un errore.
        if (string.IsNullOrEmpty(name) || string.Equals(user?["descriptor"]?.GetValue<string>(), "System:PublicAccess;aad:anonymous", StringComparison.OrdinalIgnoreCase))
            throw new AzureDevOpsException(HttpStatusCode.Unauthorized, Loc.T("Token PAT non valido o scaduto."));
        return name;
    }

    /// <summary>
    /// Organizzazioni a cui l'account appartiene (per la tendina della procedura guidata): profilo, poi account del membro.
    /// Con un PAT funziona solo se creato per "All accessible organizations".
    /// </summary>
    public async Task<(string Who, IReadOnlyList<string> Orgs)> OrganizationsAsync(AzureSettings s, CancellationToken ct)
    {
        const string vssps = "https://app.vssps.visualstudio.com/_apis";
        var me = await SendAsync(Request(HttpMethod.Get, $"{vssps}/profile/profiles/me?{Api}", s), ct);
        var id = me?["id"]?.GetValue<string>() ?? string.Empty;
        var who = me?["emailAddress"]?.GetValue<string>() ?? me?["displayName"]?.GetValue<string>() ?? string.Empty;
        if (id.Length == 0)
            return (who, []);
        var json = await SendAsync(Request(HttpMethod.Get, $"{vssps}/accounts?memberId={Uri.EscapeDataString(id)}&{Api}", s), ct);
        var orgs = (json?["value"]?.AsArray() ?? [])
            .Select(a => a?["accountName"]?.GetValue<string>() ?? string.Empty)
            .Where(n => n.Length > 0).Select(n => "https://dev.azure.com/" + n)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return (who, orgs);
    }

    /// <summary>Progetti dell'organizzazione visibili all'utente (tendina).</summary>
    public async Task<IReadOnlyList<string>> ProjectsAsync(AzureSettings s, CancellationToken ct)
    {
        var json = await SendAsync(Request(HttpMethod.Get, $"{Org(s)}/_apis/projects?$top=500&{Api}", s), ct);
        return (json?["value"]?.AsArray() ?? []).Select(p => p?["name"]?.GetValue<string>() ?? string.Empty)
            .Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Il progetto esiste ed e' visibile con questo token.</summary>
    public async Task CheckProjectAsync(AzureSettings s, CancellationToken ct)
        => await SendAsync(Request(HttpMethod.Get, $"{Org(s)}/_apis/projects/{Uri.EscapeDataString(s.Project)}?{Api}", s), ct);

    /// <summary>Work item per id (quelli che non esistono o non si vedono mancano dal risultato).</summary>
    public async Task<IReadOnlyList<WorkItemInfo>> GetAsync(AzureSettings s, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var result = new List<WorkItemInfo>();
        foreach (var chunk in ids.Distinct().Chunk(200)) // limite di workitemsbatch
        {
            var body = new JsonObject
            {
                ["ids"] = new JsonArray(chunk.Select(i => (JsonNode)i).ToArray()),
                ["fields"] = new JsonArray(Fields.Select(f => (JsonNode)f).ToArray()),
                ["errorPolicy"] = "omit",
            };
            var json = await SendAsync(Request(HttpMethod.Post, $"{Proj(s)}/_apis/wit/workitemsbatch?{Api}", s, body), ct);
            foreach (var item in json?["value"]?.AsArray() ?? [])
                if (item?["fields"] is JsonObject f && item["id"]?.GetValue<int>() is int id)
                    result.Add(new WorkItemInfo(id, Str(f, "System.WorkItemType"), Str(f, "System.Title"), Str(f, "System.State"), WorkItemText.Url(s.Org, s.Project, id)));
        }

        return result;
    }

    private static string Str(JsonObject f, string name) => f[name]?.GetValue<string>() ?? string.Empty;

    /// <summary>I miei work item aperti (assegnati a me, non chiusi), dai piu' recenti: per la tendina dopo #.</summary>
    public async Task<IReadOnlyList<WorkItemInfo>> MineAsync(AzureSettings s, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["query"] = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me " +
                        "AND [System.State] NOT IN ('Closed', 'Done', 'Removed') ORDER BY [System.ChangedDate] DESC",
        };
        var json = await SendAsync(Request(HttpMethod.Post, $"{Proj(s)}/_apis/wit/wiql?$top=50&{Api}", s, body), ct);
        var ids = (json?["workItems"]?.AsArray() ?? []).Select(w => w?["id"]?.GetValue<int>() ?? 0).Where(i => i > 0).ToList();
        var items = await GetAsync(s, ids, ct);
        return ids.Select(i => items.FirstOrDefault(w => w.Id == i)).OfType<WorkItemInfo>().ToList();
    }

    /// <summary>Stati di un tipo di work item, nell'ordine del processo, con la categoria (per la tendina "Stato dopo il commit").</summary>
    public async Task<IReadOnlyList<WorkItemState>> StatesAsync(AzureSettings s, string type, CancellationToken ct)
    {
        var json = await SendAsync(Request(HttpMethod.Get, $"{Proj(s)}/_apis/wit/workitemtypes/{Uri.EscapeDataString(type)}/states?{Api}", s), ct);
        return (json?["value"]?.AsArray() ?? [])
            .Select(v => new WorkItemState(v?["name"]?.GetValue<string>() ?? string.Empty, v?["category"]?.GetValue<string>() ?? string.Empty))
            .Where(n => n.Name.Length > 0).ToList();
    }

    /// <summary>Il passaggio allo stato e' permesso dalle regole? Prova con validateOnly (Azure non salva): null se si', altrimenti il motivo.</summary>
    public async Task<string?> ValidateStateAsync(AzureSettings s, int id, string state, CancellationToken ct)
    {
        try
        {
            await PatchAsync(s, id, [new JsonObject { ["op"] = "add", ["path"] = "/fields/System.State", ["value"] = state }], ct, validateOnly: true);
            return null;
        }
        catch (AzureDevOpsException ex)
        {
            return ex.Message;
        }
    }

    private Task<JsonNode?> PatchAsync(AzureSettings s, int id, JsonArray ops, CancellationToken ct, bool validateOnly = false)
        => SendAsync(Request(HttpMethod.Patch, $"{Proj(s)}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?{(validateOnly ? "validateOnly=true&" : "")}{Api}", s, ops, "application/json-patch+json"), ct);

    /// <summary>Inizio del commento dei link scritti da SVNAssist: serve a riconoscerli (e sostituirli) senza toccare gli altri link.</summary>
    internal const string LinkMarker = "SVN r";

    /// <summary>
    /// Link (Hyperlink nella scheda Links) con un commento che fa da "card": revisione, ramo, autore, messaggio.
    /// Sostituisce i link SVNAssist gia' presenti (commento che inizia con <see cref="LinkMarker"/>): un solo link per work item,
    /// quello del commit piu' recente (un bug riaperto e richiuso punta al nuovo fix). Gli altri link non si toccano.
    /// </summary>
    public async Task AddLinkAsync(AzureSettings s, int id, string url, string comment, CancellationToken ct)
    {
        var item = await SendAsync(Request(HttpMethod.Get, $"{Proj(s)}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?$expand=relations&{Api}", s), ct);
        var ops = new JsonArray();
        foreach (var i in OurLinks(item).OrderByDescending(i => i))
            ops.Add(new JsonObject { ["op"] = "remove", ["path"] = "/relations/" + i.ToString(CultureInfo.InvariantCulture) });
        ops.Add(new JsonObject
        {
            ["op"] = "add",
            ["path"] = "/relations/-",
            ["value"] = new JsonObject { ["rel"] = "Hyperlink", ["url"] = url, ["attributes"] = new JsonObject { ["comment"] = comment } },
        });
        await PatchAsync(s, id, ops, ct);
    }

    /// <summary>Indici (in relations) dei link scritti da SVNAssist.</summary>
    internal static IReadOnlyList<int> OurLinks(JsonNode? workItem)
    {
        var rels = workItem?["relations"]?.AsArray();
        var result = new List<int>();
        for (int i = 0; rels is not null && i < rels.Count; i++)
        {
            var r = rels[i];
            if (string.Equals(r?["rel"]?.GetValue<string>(), "Hyperlink", StringComparison.Ordinal)
                && (r?["attributes"]?["comment"]?.GetValue<string>() ?? string.Empty).StartsWith(LinkMarker, StringComparison.Ordinal))
                result.Add(i);
        }

        return result;
    }

    public Task SetStateAsync(AzureSettings s, int id, string state, CancellationToken ct)
        => PatchAsync(s, id, [new JsonObject { ["op"] = "add", ["path"] = "/fields/System.State", ["value"] = state }], ct);

    /// <summary>Commento nella Discussion (campo History).</summary>
    public Task AddCommentAsync(AzureSettings s, int id, string text, CancellationToken ct)
        => PatchAsync(s, id, [new JsonObject { ["op"] = "add", ["path"] = "/fields/System.History", ["value"] = text }], ct);

    /// <summary>Il token puo' modificare il work item? Prova una modifica con validateOnly (Azure non salva nulla).</summary>
    public Task CheckWriteAsync(AzureSettings s, int id, CancellationToken ct)
        => PatchAsync(s, id, [new JsonObject { ["op"] = "add", ["path"] = "/fields/System.History", ["value"] = "SVNAssist" }], ct, validateOnly: true);
}
