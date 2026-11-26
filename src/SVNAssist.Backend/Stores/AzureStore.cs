using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>Un controllo della procedura guidata: Ok null = da fare / in corso.</summary>
internal sealed record AzCheckDto(string Id, string Label, bool? Ok, string Detail, AzFixDto? Fix = null);

/// <summary>
/// Come sistemare un controllo fallito. Action: install | login (console), signin (accesso Windows), patpage (pagina dei token)
/// o vuoto se l'utente da solo non puo' (Text dice a chi chiedere cosa). Command: comando PowerShell da eseguire o copiare.
/// </summary>
internal sealed record AzFixDto(string Text, string Action, string Command);

/// <summary>Work item per la UI (chip nel commit, tendina, badge del Log).</summary>
internal sealed record AzItemDto(int Id, string Type, string Title, string State, string Url);

/// <summary>
/// Una voce di "Stato dopo il commit": <c>Via</c> = stati intermedi per rispettare le regole del processo; <c>Ok</c> = il primo
/// passaggio e' permesso (verificato con validateOnly); altrimenti <c>Note</c> dice perche' (es. campo obbligatorio).
/// </summary>
internal sealed record AzStateDto(string Name, IReadOnlyList<string> Via, bool Ok, string Note);

/// <summary>Procedura guidata "Collega Azure DevOps".</summary>
/// <param name="Auth">pat | cli | sso (vedi <see cref="AzureSettings"/>).</param>
internal sealed record AzWizardDto(string Org, string Project, bool HasPat, bool Busy, IReadOnlyList<AzCheckDto> Checks, bool CanEnable,
    bool Enabled, bool LinkOnCommit, bool Comment, string Auth, string ClientId, string Tenant, string Feedback,
    IReadOnlyList<string> Orgs, IReadOnlyList<string> Projects, string Who);

/// <summary>
/// Stato Azure nello snapshot di Changes: <c>Ready</c> = integrazione attiva e configurata (senza, la UI non mostra nulla di Azure
/// tranne la voce di menu per configurarla). <c>Linked</c> = work item del messaggio di commit; <c>Suggest</c> = tendina dopo #;
/// <c>States</c>/<c>TargetState</c> = "Stato dopo il commit" (vuoto = invariato).
/// </summary>
internal sealed record AzureDto(bool Ready, bool Busy, string Error, IReadOnlyList<AzItemDto> Linked,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AzItemDto>? Suggest,
    IReadOnlyList<AzStateDto> States, string TargetState,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AzWizardDto? Wizard);

/// <summary>
/// Integrazione facoltativa con Azure DevOps (protocollo: comandi <c>az*</c> di Changes; badge nel Log).
/// Spenta finche' l'utente non la configura con la procedura guidata: allora nessuna chiamata parte.
/// Il messaggio di commit resta testo leggibile ("Bug 81018 - titolo" + link, <see cref="WorkItemText"/>); dopo il commit
/// si aggiunge al work item un link alla revisione e, se scelto, si cambia lo stato. I fallimenti verso Azure non bloccano mai il commit.
/// </summary>
internal sealed class AzureStore
{
    private readonly ISvnAssistSettings _settings;
    private readonly AzureDevOpsClient _client;
    private readonly IAzureTokens _tokens;
    private string _wizFeedback = string.Empty;
    private IReadOnlyList<string> _orgs = [], _projects = [];
    private string _who = string.Empty;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<int, (WorkItemInfo Item, DateTime At)> _cache = new();
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(3);
    private (IReadOnlyList<WorkItemInfo> Items, DateTime At)? _mine;

    private bool _busy;
    private string _error = string.Empty;
    private IReadOnlyList<AzItemDto>? _suggest;
    private int _suggestGen;
    private IReadOnlyList<AzStateDto> _states = [];
    private readonly Dictionary<string, IReadOnlyList<WorkItemState>> _typeStates = new(StringComparer.OrdinalIgnoreCase);
    private string _targetState = string.Empty;
    private IReadOnlyList<int> _linkedIds = [];
    private bool _ready;

    private bool _wizOpen, _wizBusy;
    private IReadOnlyList<AzCheckDto> _checks = [];
    private AzureSettings _wizSettings = AzureSettings.Off;

    public AzureStore(ISvnAssistSettings settings, HttpClient http, IAzureTokens? tokens = null, Func<DateTime>? now = null)
    {
        _settings = settings;
        _tokens = tokens ?? new PatTokens();
        _client = new AzureDevOpsClient(http, _tokens);
        _now = now ?? (() => DateTime.UtcNow);
    }

    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    internal Task<AzureSettings> SettingsAsync(CancellationToken ct) => _settings.GetAzureAsync(ct);

    /// <summary>Rilegge le impostazioni (avvio, Opzioni cambiate): decide se la UI mostra Azure.</summary>
    internal async Task RefreshReadyAsync(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        lock (_lock)
        {
            if (_ready == s.Ready)
                return;
            _ready = s.Ready;
            if (!_ready) { _suggest = null; _states = []; _targetState = string.Empty; _error = string.Empty; }
        }

        Notify();
    }

    internal AzureDto GetSnapshot()
    {
        lock (_lock)
        {
            var linked = _linkedIds.Select(id => _cache.TryGetValue(id, out var c) ? Dto(c.Item) : new AzItemDto(id, string.Empty, string.Empty, string.Empty, string.Empty)).ToList();
            var wiz = _wizOpen
                ? new AzWizardDto(_wizSettings.Org, _wizSettings.Project, _wizSettings.Pat.Length > 0, _wizBusy, _checks,
                    _checks.Count > 0 && _checks.Where(c => c.Id != "write").All(c => c.Ok == true), _wizSettings.Enabled, _wizSettings.LinkOnCommit, _wizSettings.Comment,
                    _wizSettings.Auth, _wizSettings.ClientId, _wizSettings.Tenant, _wizFeedback,
                    _orgs, _projects, _who)
                : null;
            return new AzureDto(_ready, _busy, _error, linked, _suggest, _states, _targetState, wiz);
        }
    }

    private static AzItemDto Dto(WorkItemInfo w) => new(w.Id, w.Type, w.Title, w.State, w.Url);

    // ── Commit ──────────────────────────────────────────────────────

    /// <summary>Il messaggio di commit e' cambiato: aggiorna i work item collegati (dai link nel testo) e la tendina dopo #.</summary>
    internal async Task OnMessageChangedAsync(string message, CancellationToken ct)
    {
        var ids = WorkItemText.IdsIn(message);
        bool changed;
        lock (_lock)
        {
            changed = !ids.SequenceEqual(_linkedIds);
            _linkedIds = ids;
            if (ids.Count == 0) { _states = []; _targetState = string.Empty; }
        }

        if (changed)
            Notify();
        if (!_ready)
            return;
        if (WorkItemText.HashQuery(message) is { } q)
            await SuggestAsync(q.Query, ct);
        else if (_suggest is not null)
        {
            lock (_lock) _suggest = null;
            Notify();
        }

        if (changed && ids.Count > 0)
            await EnsureCachedAsync(ids, ct);
    }

    private async Task EnsureCachedAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        try
        {
            await GetItemsAsync(ids, ct);
            Notify();
        }
        catch (AzureDevOpsException ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>Work item con cache di qualche minuto: una chiamata per tutti quelli che mancano.</summary>
    internal async Task<IReadOnlyList<WorkItemInfo>> GetItemsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var now = _now();
        List<int> missing;
        lock (_lock)
            missing = ids.Where(i => !_cache.TryGetValue(i, out var c) || now - c.At > CacheAge).Distinct().ToList();
        if (missing.Count > 0)
        {
            var s = await SettingsAsync(ct);
            if (!s.Ready)
                return [];
            var fetched = await _client.GetAsync(s, missing, ct);
            lock (_lock)
                foreach (var w in fetched)
                    _cache[w.Id] = (w, now);
        }

        lock (_lock)
            return ids.Where(_cache.ContainsKey).Select(i => _cache[i].Item).ToList();
    }

    /// <summary>
    /// Tendina dopo #: i miei work item aperti che iniziano con le cifre scritte (o le contengono nel titolo), piu' il numero esatto
    /// se e' lungo almeno 4 cifre e non e' tra i miei.
    /// </summary>
    internal async Task SuggestAsync(string query, CancellationToken ct)
    {
        int gen;
        lock (_lock) gen = ++_suggestGen;
        try
        {
            var s = await SettingsAsync(ct);
            if (!s.Ready) return;
            var now = _now();
            IReadOnlyList<WorkItemInfo> mine;
            lock (_lock) mine = _mine is { } m && now - m.At < CacheAge ? m.Items : [];
            if (mine.Count == 0)
            {
                mine = await _client.MineAsync(s, ct);
                lock (_lock)
                {
                    _mine = (mine, now);
                    foreach (var w in mine) _cache[w.Id] = (w, now);
                }
            }

            var list = mine.Where(w => w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal)).Take(8).ToList();
            if (query.Length >= 4 && int.TryParse(query, out var exact) && list.All(w => w.Id != exact))
                list.InsertRange(0, await GetItemsAsync([exact], ct));
            lock (_lock)
            {
                if (gen != _suggestGen) return; // nel frattempo l'utente ha scritto altro
                _suggest = list.Select(Dto).ToList();
                _error = string.Empty;
            }
        }
        catch (AzureDevOpsException ex)
        {
            SetError(ex.Message);
            return;
        }

        Notify();
    }

    /// <summary>
    /// Casella "Work item" (Cerca) o scelta nella tendina: legge il work item e restituisce il messaggio con la sua intestazione
    /// (togliendo l'eventuale "#cifre" in fondo); null se non trovato (errore nello stato).
    /// </summary>
    internal async Task<string?> LinkAsync(int id, string message, CancellationToken ct)
    {
        lock (_lock) { _busy = true; _error = string.Empty; }
        Notify();
        try
        {
            var item = (await GetItemsAsync([id], ct)).FirstOrDefault();
            if (item is null)
            {
                SetError(Loc.T("Work item {0} non trovato nel progetto.", id));
                return null;
            }

            if (WorkItemText.HashQuery(message) is { } q)
                message = message[..q.Start].TrimEnd();
            var result = WorkItemText.WithHeader(message, item);
            await LoadStatesAsync(item, ct);
            lock (_lock) _suggest = null;
            return result;
        }
        catch (AzureDevOpsException ex)
        {
            SetError(ex.Message);
            return null;
        }
        finally
        {
            lock (_lock) _busy = false;
            Notify();
        }
    }

    private async Task<IReadOnlyList<WorkItemState>> TypeStatesAsync(AzureSettings s, string type, CancellationToken ct)
    {
        lock (_lock)
            if (_typeStates.TryGetValue(type, out var cached))
                return cached;
        var states = await _client.StatesAsync(s, type, ct);
        lock (_lock)
            _typeStates[type] = states;
        return states;
    }

    /// <summary>
    /// Stati proposti per il work item appena collegato: per ognuno la strada (stati intermedi) e se il primo passaggio e' permesso,
    /// provato "a vuoto" con validateOnly. Cosi' una regola del processo si vede prima del commit, non dopo.
    /// </summary>
    private async Task LoadStatesAsync(WorkItemInfo item, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var states = await TypeStatesAsync(s, item.Type, ct);
        var options = new List<AzStateDto>();
        foreach (var st in states.Where(x => !string.Equals(x.Name, item.State, StringComparison.OrdinalIgnoreCase)))
        {
            var steps = StatePath.Steps(states, item.State, st.Name);
            var why = await _client.ValidateStateAsync(s, item.Id, steps[0], ct);
            options.Add(new AzStateDto(st.Name, steps.Take(steps.Count - 1).ToList(), why is null, why is null ? string.Empty : Loc.T("{0} non permesso adesso: {1}", steps[0], why)));
        }

        lock (_lock) _states = options;
    }

    internal void SetTargetState(string state)
    {
        lock (_lock)
            _targetState = state.Length == 0 || _states.Any(x => x.Name == state && x.Ok) ? state : _targetState;
        Notify();
    }

    /// <summary>Porta il work item allo stato passando per gli intermedi; si ferma al primo passaggio rifiutato. null = fatto, altrimenti il motivo.</summary>
    private async Task<string?> MoveToStateAsync(AzureSettings s, int id, string target, CancellationToken ct)
    {
        var item = (await _client.GetAsync(s, [id], ct)).FirstOrDefault();
        if (item is null)
            return Loc.T("work item non trovato");
        var states = await TypeStatesAsync(s, item.Type, ct);
        var at = item.State;
        foreach (var step in StatePath.Steps(states, item.State, target))
        {
            try
            {
                await _client.SetStateAsync(s, id, step, ct);
                at = step;
            }
            catch (AzureDevOpsException ex)
            {
                return Loc.T("fermo su {0}, passaggio a {1} rifiutato: {2}", at, step, ex.Message);
            }
        }

        lock (_lock) _cache.Remove(id); // lo stato e' cambiato: il prossimo badge lo rilegge
        return null;
    }

    internal void CloseSuggest()
    {
        lock (_lock) _suggest = null;
        Notify();
    }

    private void SetError(string message)
    {
        lock (_lock) { _error = message; _busy = false; }
        Notify();
    }

    /// <summary>
    /// Dopo un commit riuscito: per ogni work item del messaggio un link alla revisione (con ramo e ramo di origine nel commento),
    /// lo stato scelto e, se attivo, un commento. Solo i work item scritti nel messaggio: un merge non aggiunge link alle revisioni che porta.
    /// Restituisce un riepilogo (null se non c'era nulla da fare); gli errori finiscono nel riepilogo, il commit resta valido.
    /// </summary>
    internal async Task<string?> AfterCommitAsync(CommitInfo c, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        if (!s.Ready)
            return null;
        var ids = WorkItemText.IdsIn(c.Message);
        var target = c.TargetState;
        var done = new List<string>();
        var errors = new List<string>();
        var summary = WorkItemText.Summary(c.Message);
        var where = c.Origin.Length > 0 ? Loc.T("su {0} (da {1})", c.Branch, c.Origin) : Loc.T("su {0}", c.Branch);
        foreach (var id in ids)
        {
            try
            {
                if (s.LinkOnCommit)
                    await _client.AddLinkAsync(s, id, c.RevisionUrl, $"SVN r{c.Revision} {where} · {c.Author}" + (summary.Length > 0 ? " · " + summary : ""), ct);
                if (target.Length > 0 && await MoveToStateAsync(s, id, target, ct) is { } stuck)
                    errors.Add($"#{id}: {stuck}");
                if (s.Comment)
                    await _client.AddCommentAsync(s, id, Loc.T("{0} ha committato r{1} {2}: {3}", c.Author, c.Revision, where, summary), ct);
                done.Add("#" + id);
            }
            catch (AzureDevOpsException ex)
            {
                errors.Add($"#{id}: {ex.Message}");
            }
        }

        if (done.Count == 0 && errors.Count == 0)
            return null;
        var text = done.Count > 0 ? Loc.T("Azure DevOps aggiornato: {0}.", string.Join(", ", done)) : string.Empty;
        if (errors.Count > 0)
            text = (text + " " + Loc.T("Problemi con Azure DevOps: {0}", string.Join("; ", errors))).Trim();
        return text;
    }

    /// <summary>Dati del commit per i link in Azure.</summary>
    /// <param name="RevisionUrl">URL https del ramo alla revisione (<c>...?p=N</c>): nel browser mostra la cartella com'era.</param>
    /// <param name="TargetState">"Stato dopo il commit" scelto (vuoto = invariato), preso con <see cref="TakeTargetState"/> prima di svuotare il messaggio.</param>
    internal sealed record CommitInfo(long Revision, string Message, string Author, string Branch, string Origin, string RevisionUrl, string TargetState = "");

    /// <summary>Lo stato scelto per il commit appena fatto; la scelta si azzera (vale per un commit).</summary>
    internal string TakeTargetState()
    {
        lock (_lock)
        {
            var t = _targetState;
            _targetState = string.Empty;
            return t;
        }
    }

    /// <summary>URL della pagina del work item, costruito dalle impostazioni (mai preso dalla UI). null se l'integrazione non e' configurata.</summary>
    internal async Task<string?> WorkItemUrlAsync(long id, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        return s.Org.Length > 0 && s.Project.Length > 0 && id is > 0 and <= int.MaxValue ? WorkItemText.Url(s.Org, s.Project, (int)id) : null;
    }

    /// <summary>Pagina di Azure DevOps dove si crea il token PAT (per la procedura guidata).</summary>
    internal static string PatPageUrl(string org) => AzureSettings.NormalizeOrg(org) is { Length: > 0 } o ? o + "/_usersSettings/tokens" : "https://dev.azure.com";

    // ── Procedura guidata ───────────────────────────────────────────

    internal async Task OpenWizardAsync(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        lock (_lock) { _wizOpen = true; _wizSettings = s; _checks = []; _wizBusy = true; _wizFeedback = string.Empty; _orgs = []; _projects = []; _who = string.Empty; }
        Notify();
        try
        {
            // Prima configurazione: se Azure CLI ha gia' fatto l'accesso lo uso senza chiedere nulla.
            if (s.Org.Length == 0 && s.Pat.Length == 0 && s.Auth == "pat" && await DiscoverAsync(s with { Auth = "cli" }, quiet: true, ct))
                await _settings.SetSettingAsync(SettingDefinitions.AzAuth, "cli", ct);
            else
                await DiscoverAsync(s, quiet: true, ct);
        }
        finally
        {
            var now = await SettingsAsync(ct);
            lock (_lock) { _wizSettings = now; _wizBusy = false; }
            Notify();
        }
    }

    /// <summary>"azDiscover": salva modo di accesso e organizzazione scritti, poi ricarica le tendine.</summary>
    internal async Task DiscoverFromUiAsync(string org, string auth, string clientId, string tenant, CancellationToken ct)
    {
        await _settings.SetSettingAsync(SettingDefinitions.AzAuth, AzureSettings.AuthModes.Contains(auth) ? auth : "pat", ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzOrg, AzureSettings.NormalizeOrg(org), ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzClientId, (clientId ?? string.Empty).Trim(), ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzTenant, (tenant ?? string.Empty).Trim(), ct);
        var s = await SettingsAsync(ct);
        lock (_lock) { _wizSettings = s; _wizBusy = true; }
        Notify();
        try { await DiscoverAsync(s, quiet: false, ct); }
        finally
        {
            var now = await SettingsAsync(ct);
            lock (_lock) { _wizSettings = now; _wizBusy = false; }
            Notify();
        }
    }

    /// <summary>
    /// Riempie le tendine: organizzazioni dell'account e progetti dell'organizzazione scelta. Se ce n'e' una sola la sceglie da solo.
    /// true se l'accesso funziona. Con quiet gli errori non si mostrano (tentativo automatico).
    /// </summary>
    private async Task<bool> DiscoverAsync(AzureSettings s, bool quiet, CancellationToken ct)
    {
        if (s.Auth == "pat" && s.Pat.Length == 0 || s.Auth == "sso" && s.ClientId.Length == 0)
            return false;
        try
        {
            var (who, orgs) = await _client.OrganizationsAsync(s, ct);
            lock (_lock) { _who = who; _orgs = orgs; _wizFeedback = who.Length > 0 ? Loc.T("Accesso come {0}", who) : _wizFeedback; }
            if (s.Org.Length == 0 && orgs.Count == 1)
            {
                await _settings.SetSettingAsync(SettingDefinitions.AzOrg, orgs[0], ct);
                s = s with { Org = orgs[0] };
            }
        }
        catch (AzureDevOpsException ex)
        {
            BackendLog.Info("Azure: organizzazioni non lette: " + ex.Message);
            // PAT limitato a una organizzazione: niente elenco, ma l'organizzazione scritta puo' funzionare lo stesso.
            if (s.Org.Length == 0 || ex.Code is "cli-missing" or "cli-login" or "sso-login" or "sso-clientid")
            {
                if (!quiet) lock (_lock) _wizFeedback = ex.Message;
                return false;
            }
        }

        if (s.Org.Length == 0)
            return true;
        try
        {
            var projects = await _client.ProjectsAsync(s, ct);
            lock (_lock) _projects = projects;
            if ((s.Project.Length == 0 || !projects.Contains(s.Project, StringComparer.OrdinalIgnoreCase)) && projects.Count == 1)
                await _settings.SetSettingAsync(SettingDefinitions.AzProject, projects[0], ct);
            return true;
        }
        catch (AzureDevOpsException ex)
        {
            lock (_lock) { _projects = []; if (!quiet) _wizFeedback = ex.Message; }
            return false;
        }
    }

    /// <summary>Aiuto per un controllo fallito: cosa fare da qui (pulsante/comando) o a chi chiedere.</summary>
    internal static AzFixDto? FixFor(string check, string code, string auth)
    {
        const string install = "winget install --id Microsoft.AzureCLI -e", login = "az login --allow-no-subscriptions";
        switch (code)
        {
            case "cli-missing": return new(Loc.T("Installa Azure CLI (una volta sola), poi fai l'accesso."), "install", install);
            case "cli-login": return new(Loc.T("Accedi con l'account aziendale nel browser, poi premi Verifica."), "login", login);
            case "sso-login": return new(Loc.T("Premi Accedi e completa la finestra di Windows con l'account aziendale."), "signin", string.Empty);
            case "sso-clientid":
                return new(Loc.T("Serve una registrazione app in Entra ID: chiedi all'IT il client id (app pubblica, permesso delegato Azure DevOps user_impersonation, redirect del broker di Windows). Intanto puoi usare Azure CLI."), string.Empty, string.Empty);
        }

        if (check == "org")
            return code == "auth"
                ? auth switch
                {
                    "cli" => new(Loc.T("Rifai l'accesso con l'account che usi in Azure DevOps."), "login", login),
                    "sso" => new(Loc.T("Rifai l'accesso con l'account che usi in Azure DevOps."), "signin", string.Empty),
                    _ => new(Loc.T("Crea un nuovo token (Work Items: Read & write) e incollalo qui."), "patpage", string.Empty),
                }
                : new(Loc.T("Controlla l'indirizzo (https://dev.azure.com/nome) o scegli l'organizzazione dalla tendina."), string.Empty, string.Empty);
        if (check == "project")
            return new(Loc.T("Scegli il progetto dalla tendina: sono quelli che vedi con il tuo account."), string.Empty, string.Empty);
        if (check is "read" or "write" && auth == "pat" && code is "forbidden" or "auth")
            return new(Loc.T("Ricrea il token con il permesso Work Items: Read & write."), "patpage", string.Empty);
        if (check == "read")
            return new(Loc.T("Il tuo utente non vede i work item del progetto: chiedi all'amministratore del progetto di aggiungerti al team (livello di accesso Basic o Stakeholder)."), string.Empty, string.Empty);
        if (check == "write")
            return new(Loc.T("Puoi leggere ma non modificare: chiedi all'amministratore del progetto il permesso \"Edit work items in this node\" sull'Area Path dei tuoi work item. Intanto i pulsanti restano disattivati."), string.Empty, string.Empty);
        return null;
    }

    internal void CloseWizard()
    {
        lock (_lock) { _wizOpen = false; _checks = []; }
        Notify();
    }

    /// <summary>
    /// "Verifica": salva organizzazione, progetto e (se scritto) il token, poi controlla in ordine: organizzazione e token,
    /// progetto, lettura dei work item, scrittura (validateOnly su un mio work item: Azure non salva nulla).
    /// </summary>
    internal async Task CheckAsync(string org, string project, string? pat, string auth, string clientId, string tenant, CancellationToken ct)
    {
        org = AzureSettings.NormalizeOrg(org);
        project = (project ?? string.Empty).Trim();
        auth = AzureSettings.AuthModes.Contains(auth) ? auth : "pat";
        await _settings.SetSettingAsync(SettingDefinitions.AzOrg, org, ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzProject, project, ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzAuth, auth, ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzClientId, (clientId ?? string.Empty).Trim(), ct);
        await _settings.SetSettingAsync(SettingDefinitions.AzTenant, (tenant ?? string.Empty).Trim(), ct);
        if (!string.IsNullOrWhiteSpace(pat))
            await _settings.SetSettingAsync(SettingDefinitions.AzPat, pat.Trim(), ct);
        await RunChecksAsync(ct);
    }

    /// <summary>
    /// Accesso Windows (SSO): apre la finestra di accesso di Windows per l'account aziendale (una volta; poi e' silenzioso),
    /// quindi rifa' i controlli.
    /// </summary>
    internal async Task SignInAsync(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        lock (_lock) { _wizBusy = true; _wizFeedback = Loc.T("Accesso in corso: completa la finestra di Windows..."); }
        Notify();
        try
        {
            var who = await _tokens.SignInAsync(s, ct);
            lock (_lock) _wizFeedback = who.Length > 0 ? Loc.T("Accesso fatto: {0}", who) : string.Empty;
        }
        catch (AzureDevOpsException ex)
        {
            lock (_lock) { _wizFeedback = ex.Message; _wizBusy = false; }
            Notify();
            return;
        }

        await RunChecksAsync(ct);
    }

    /// <summary>Installa Azure CLI o fa "az login" in una console visibile (l'utente completa li', poi preme Verifica).</summary>
    internal void RunCli(string what)
    {
        var ok = ConsoleLauncher.Launch(what == "install" ? ConsoleCommand.InstallAzureCli : ConsoleCommand.AzureLogin);
        lock (_lock)
            _wizFeedback = !ok ? Loc.T("Non riesco ad aprire la console.")
                : what == "install" ? Loc.T("Installazione di Azure CLI avviata nella console: al termine premi Accedi (az login).")
                : Loc.T("Completa l'accesso nel browser aperto da az login, poi premi Verifica.");
        Notify();
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var org = AzureSettings.NormalizeOrg(s.Org);
        var project = s.Project;
        var checks = new List<AzCheckDto>
        {
            new("org", s.Auth switch { "cli" => Loc.T("Organizzazione e accesso (Azure CLI)"), "sso" => Loc.T("Organizzazione e accesso (Windows)"), _ => Loc.T("Organizzazione e token PAT") }, null, org),
            new("project", Loc.T("Progetto"), null, project),
            new("read", Loc.T("Lettura work item"), null, Loc.T("badge, ricerca, titoli")),
            new("write", Loc.T("Scrittura work item"), null, Loc.T("link alle revisioni e cambio stato")),
        };
        lock (_lock) { _wizSettings = s; _checks = checks.ToList(); _wizBusy = true; }
        Notify();

        void Set(string id, bool ok, string detail, AzFixDto? fix = null)
        {
            lock (_lock)
                _checks = _checks.Select(c => c.Id == id ? c with { Ok = ok, Detail = detail, Fix = fix } : c).ToList();
            Notify();
        }

        void Fail(string id, AzureDevOpsException ex) => Set(id, false, ex.Message, FixFor(id, ex.Code, s.Auth));

        try
        {
            if (!s.Configured)
            {
                Set("org", false, s.Auth == "sso" ? Loc.T("Compila organizzazione, progetto e client id dell'app Entra ID.") : s.Auth == "cli" ? Loc.T("Compila organizzazione e progetto.") : Loc.T("Compila organizzazione, progetto e token PAT."),
                    s.Auth == "sso" && s.ClientId.Length == 0 ? FixFor("org", "sso-clientid", s.Auth) : s.Auth == "pat" && s.Pat.Length == 0 ? FixFor("org", "auth", s.Auth) : null);
                return;
            }

            try { Set("org", true, Loc.T("{0} · utente {1}", org, await _client.CheckUserAsync(s, ct))); }
            catch (AzureDevOpsException ex) { Fail("org", ex); return; }

            await DiscoverAsync(s, quiet: true, ct);
            try { await _client.CheckProjectAsync(s, ct); Set("project", true, project); }
            catch (AzureDevOpsException ex) { Fail("project", ex); return; }

            IReadOnlyList<WorkItemInfo> mine;
            try
            {
                mine = await _client.MineAsync(s, ct);
                Set("read", true, Loc.T("{0} work item aperti assegnati a te", mine.Count));
            }
            catch (AzureDevOpsException ex) { Fail("read", ex); return; }

            if (mine.Count == 0)
            {
                Set("write", false, Loc.T("Non verificabile ora: nessun work item assegnato a te. Si vedrà al primo commit collegato."));
                return;
            }

            try { await _client.CheckWriteAsync(s, mine[0].Id, ct); Set("write", true, Loc.T("verificata su #{0}, senza modificarlo", mine[0].Id)); }
            catch (AzureDevOpsException ex) { Fail("write", ex); }
        }
        finally
        {
            lock (_lock) _wizBusy = false;
            Notify();
        }
    }

    /// <summary>Attiva/disattiva l'integrazione o una sua opzione (chiavi <see cref="SettingDefinitions"/>.Az*).</summary>
    internal async Task SetOptionAsync(string key, bool on, CancellationToken ct)
    {
        await _settings.SetSettingAsync(key, on ? "true" : "false", ct);
        var s = await SettingsAsync(ct);
        lock (_lock) _wizSettings = s;
        await RefreshReadyAsync(ct);
        Notify();
    }
}
