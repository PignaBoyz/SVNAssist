using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SVNAssist.Services;

/// <summary>
/// Un modello AI scelto dall'elenco: <c>Id</c> e' quello che si manda al provider, <c>Label</c> il nome per l'utente.
/// <c>Cost</c> e' il costo dichiarato dal provider (es. moltiplicatore di richieste premium di Copilot), vuoto se il provider non lo espone: mai stimato.
/// </summary>
internal sealed record AiModelInfo(string Id, string Label, string Cost = "");

/// <summary>Esito di una richiesta riuscita al provider: i modelli e, se il provider lo dice, l'account collegato (es. login di Copilot).</summary>
internal sealed record AiModelsFetched(IReadOnlyList<AiModelInfo> Models, string? Account = null);

/// <summary>
/// Elenco dei modelli di un provider. <c>Source</c>: <c>live</c> (appena chiesto al provider), <c>cache</c> (copia salvata, con la sua data)
/// o <c>fallback</c> (elenco predefinito dell'app: il provider non ha risposto o non permette di elencare).
/// <c>Account</c> e' l'account con cui il provider ha risposto (null se non noto).
/// <c>Error</c> e' il motivo per cui l'elenco aggiornato non c'e' (null se tutto ok o se l'elenco predefinito e' normale per quel provider).
/// </summary>
internal sealed record AiModelList(IReadOnlyList<AiModelInfo> Models, DateTimeOffset? UpdatedAt, string Source, string? Error, string? Account = null);

/// <summary>Elenco dei modelli realmente disponibili per provider (<c>copilot</c>, <c>claudecode</c>, <c>claude</c>, <c>openai</c>).</summary>
internal interface IAiModelCatalog
{
    /// <param name="forceRefresh">Ignora la copia salvata e chiede sempre al provider.</param>
    /// <param name="maxAge">Eta' massima della copia salvata (default 24 ore).</param>
    Task<AiModelList> GetAsync(string provider, bool forceRefresh, CancellationToken ct, TimeSpan? maxAge = null);
}

/// <summary>Come si ottiene l'elenco da un provider (rete/SDK): separato dalla cache per poterlo sostituire nei test.</summary>
internal interface IAiModelSource
{
    /// <summary>Parte della chiave di cache oltre al provider (per OpenAI-compatibile: l'endpoint). Mai la chiave API.</summary>
    Task<string> IdentityAsync(string provider, CancellationToken ct);

    /// <summary>Elenco dal provider; <c>null</c> = questo provider non permette di elencare (si usa l'elenco predefinito). Lancia se la richiesta fallisce.</summary>
    Task<AiModelsFetched?> ListAsync(string provider, bool force, CancellationToken ct);
}

/// <summary>Valida gli id modello scelti dall'utente (elenco, "Altro...", Options): niente spazi, niente caratteri strani.</summary>
internal static class AiModelIds
{
    private static readonly Regex Plausible = new(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-]{0,199}$", RegexOptions.Compiled);

    internal static bool IsPlausible(string? id) => id is not null && Plausible.IsMatch(id.Trim());
}

/// <summary>
/// Cache datata (24 ore, forzabile) dell'elenco modelli: un provider che ritira un modello (es. Copilot) non lascia l'utente bloccato su
/// un elenco fisso. Un errore non entra mai in cache: si rimanda l'ultima copia buona (con la sua data) o l'elenco predefinito.
/// </summary>
internal sealed class AiModelCatalog : IAiModelCatalog
{
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

    private sealed record Entry(DateTimeOffset UpdatedAt, List<AiModelInfo> Models, string? Account);

    private readonly IAiModelSource? _source;
    private readonly string? _cacheFile;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, Entry>? _entries;

    /// <param name="source">Come elencare; null = solo elenco predefinito (test e usi senza rete).</param>
    /// <param name="cacheFile">File JSON della cache su disco; null = solo in memoria (i test non devono scrivere in %LOCALAPPDATA%).</param>
    public AiModelCatalog(IAiModelSource? source = null, string? cacheFile = null, Func<DateTimeOffset>? now = null)
    {
        _source = source;
        _cacheFile = cacheFile;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>File della cache nel profilo dell'utente.</summary>
    internal static string DefaultCacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "ai-models-cache.json");

    /// <inheritdoc />
    public async Task<AiModelList> GetAsync(string provider, bool forceRefresh, CancellationToken ct, TimeSpan? maxAge = null)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var fallback = FallbackModels(provider);
        if (_source is null || fallback.Count == 0)
            return new AiModelList(fallback, null, "fallback", null);

        await _gate.WaitAsync(ct);
        try
        {
            var identity = string.Empty;
            try { identity = await _source.IdentityAsync(provider, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { BackendLog.Warn("Identita' elenco modelli: " + ex.Message); }

            var key = provider + "|" + identity;
            var entries = LoadEntries();
            entries.TryGetValue(key, out var entry);
            if (!forceRefresh && entry is not null && _now() - entry.UpdatedAt < (maxAge ?? DefaultMaxAge))
                return new AiModelList(entry.Models, entry.UpdatedAt, "cache", null, entry.Account);

            try
            {
                var live = await _source.ListAsync(provider, forceRefresh, ct);
                if (live is null)
                    return new AiModelList(fallback, null, "fallback", null);
                if (live.Models.Count == 0)
                    throw new InvalidOperationException(Loc.T("Il provider ha risposto con un elenco di modelli vuoto."));

                var fresh = new Entry(_now(), live.Models.ToList(), live.Account);
                entries[key] = fresh;
                Save(entries);
                return new AiModelList(fresh.Models, fresh.UpdatedAt, "live", null, fresh.Account);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                BackendLog.Warn($"Elenco modelli {provider} non disponibile: {ex.Message}");
                return entry is not null
                    ? new AiModelList(entry.Models, entry.UpdatedAt, "cache", ex.Message, entry.Account)
                    : new AiModelList(fallback, null, "fallback", ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, Entry> LoadEntries()
    {
        if (_entries is not null)
            return _entries;

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (_cacheFile is not null)
        {
            try
            {
                if (File.Exists(_cacheFile))
                    foreach (var (key, entry) in ParseCacheFile(File.ReadAllText(_cacheFile)))
                        entries[key] = entry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                BackendLog.Warn("Cache elenco modelli illeggibile, la ignoro: " + ex.Message);
            }
        }

        return _entries = entries;
    }

    private void Save(Dictionary<string, Entry> entries)
    {
        if (_cacheFile is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, SerializeCacheFile(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackendLog.Warn("Cache elenco modelli non salvata: " + ex.Message);
        }
    }

    private static string SerializeCacheFile(Dictionary<string, Entry> entries)
        => JsonSerializer.Serialize(new
        {
            version = 1,
            entries = entries.ToDictionary(e => e.Key, e => new
            {
                updated = e.Value.UpdatedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                account = e.Value.Account,
                models = e.Value.Models.Select(m => new { id = m.Id, label = m.Label, cost = m.Cost }).ToArray(),
            }),
        });

    private static IEnumerable<(string Key, Entry Entry)> ParseCacheFile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var e in entries.EnumerateObject())
        {
            if (e.Value.ValueKind != JsonValueKind.Object
                || !e.Value.TryGetProperty("updated", out var u) || !DateTimeOffset.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var updated)
                || !e.Value.TryGetProperty("models", out var ms) || ms.ValueKind != JsonValueKind.Array)
                continue;

            var models = new List<AiModelInfo>();
            foreach (var m in ms.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && AiModelIds.IsPlausible(id.GetString()))
                    models.Add(new AiModelInfo(id.GetString()!, m.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : id.GetString()!,
                        m.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : string.Empty));
            if (models.Count > 0)
                yield return (e.Name, new Entry(updated, models, Str(e.Value, "account")));
        }
    }

    /// <summary>
    /// Elenco minimo dell'app, solo quando il provider non risponde o non permette di elencare: NON e' la lista dei modelli che esistono
    /// (quella viene sempre dal provider), per questo la UI offre anche "Altro...". Nessun modello e' "consigliato": sceglie l'utente.
    /// Copilot ha solo il predefinito (lo sceglie Copilot per l'account): gli id che avevamo ipotizzato non erano verificati.
    /// </summary>
    internal static IReadOnlyList<AiModelInfo> FallbackModels(string provider) => provider switch
    {
        "copilot" => [new(SettingDefinitions.CopilotDefaultModel, Loc.T("Predefinito di Copilot (lo sceglie Copilot per il tuo account)"))],
        "claudecode" or "claude" => [new("claude-sonnet-5-5", "Claude Sonnet 5.5"), new("claude-haiku-5-5", "Claude Haiku 5.5"), new("claude-opus-5-5", "Claude Opus 5.5")],
        "openai" => [new("openai/gpt-4o-mini", "GPT-4o mini"), new("openai/gpt-4o", "GPT-4o"), new("openai/gpt-4.1-mini", "GPT-4.1 mini"), new("openai/gpt-4.1", "GPT-4.1")],
        _ => [],
    };

    // ── Parser delle risposte dei provider (puri, provati con JSON di esempio) ──

    /// <summary>Anthropic <c>GET /v1/models</c>: <c>{data:[{id,display_name}], has_more, last_id}</c>.</summary>
    internal static (List<AiModelInfo> Models, bool HasMore, string? LastId) ParseAnthropicModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<AiModelInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("not a models list");

        foreach (var m in data.EnumerateArray())
        {
            var id = Str(m, "id");
            if (!AiModelIds.IsPlausible(id))
                continue;
            var name = Str(m, "display_name");
            models.Add(new AiModelInfo(id!.Trim(), string.IsNullOrWhiteSpace(name) ? id!.Trim() : name!.Trim()));
        }

        var hasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        return (models, hasMore, Str(doc.RootElement, "last_id"));
    }

    /// <summary>Catalogo GitHub Models: array (o <c>{data|models:[...]}</c>) di <c>{id: "publisher/nome", name, supported_output_modalities}</c>; salta quelli che non producono testo (embedding).</summary>
    internal static List<AiModelInfo> ParseGitHubModelsCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement;
        if (items.ValueKind == JsonValueKind.Object)
            items = items.TryGetProperty("data", out var d) ? d : items.TryGetProperty("models", out var ms) ? ms : default;
        if (items.ValueKind != JsonValueKind.Array)
            throw new JsonException("not a catalog");

        var models = new List<AiModelInfo>();
        foreach (var m in items.EnumerateArray())
        {
            var id = Str(m, "id");
            if (!AiModelIds.IsPlausible(id))
                continue;
            if (m.TryGetProperty("supported_output_modalities", out var outs) && outs.ValueKind == JsonValueKind.Array
                && !outs.EnumerateArray().Any(o => string.Equals(o.GetString(), "text", StringComparison.OrdinalIgnoreCase)))
                continue;
            var name = Str(m, "name");
            models.Add(new AiModelInfo(id!.Trim(), string.IsNullOrWhiteSpace(name) ? id!.Trim() : name!.Trim()));
        }

        return models;
    }

    /// <summary>OpenAI-compatibile <c>GET {base}/models</c>: <c>{data:[{id}]}</c> (o un array); ordina per nome e toglie embedding/audio/immagini.</summary>
    internal static List<AiModelInfo> ParseOpenAiModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement;
        if (items.ValueKind == JsonValueKind.Object)
            items = items.TryGetProperty("data", out var d) ? d : default;
        if (items.ValueKind != JsonValueKind.Array)
            throw new JsonException("not a models list");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<AiModelInfo>();
        foreach (var m in items.EnumerateArray())
        {
            var id = Str(m, "id")?.Trim();
            if (!AiModelIds.IsPlausible(id) || NotForChat.IsMatch(id!) || !seen.Add(id!))
                continue;
            models.Add(new AiModelInfo(id!, id!));
        }

        return models.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly Regex NotForChat = new("embed|whisper|tts|dall-e|moderation", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Modelli di Copilot (<c>ListModelsAsync</c>): tutti quelli abilitati per l'account (i disabilitati da piano/organizzazione non si offrono);
    /// "Predefinito" resta sempre primo. Il costo e' il moltiplicatore di richieste premium dichiarato da Copilot, mai stimato.
    /// </summary>
    internal static List<AiModelInfo> BuildCopilotModels(IEnumerable<(string Id, string? Name, string? State, double? Multiplier)> raw, string? defaultModelId = null)
    {
        var list = raw.ToList();
        // "Predefinito": se il runtime ha detto quale modello usa da solo, lo si scrive (l'utente vuole sapere quale sara' usato).
        var defName = defaultModelId is null ? null : list.FirstOrDefault(m => string.Equals(m.Id, defaultModelId, StringComparison.OrdinalIgnoreCase)).Name;
        var defLabel = string.IsNullOrWhiteSpace(defaultModelId)
            ? Loc.T("Predefinito di Copilot (lo sceglie Copilot per il tuo account)")
            : Loc.T("Predefinito di Copilot: {0}", string.IsNullOrWhiteSpace(defName) ? defaultModelId : defName + " (" + defaultModelId + ")");
        var models = new List<AiModelInfo> { new(SettingDefinitions.CopilotDefaultModel, defLabel) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SettingDefinitions.CopilotDefaultModel };
        foreach (var (id, name, state, multiplier) in list)
        {
            if (!AiModelIds.IsPlausible(id) || !seen.Add(id.Trim()))
                continue;
            // Tutti i modelli che Copilot elenca: quelli non abilitati restano visibili con il motivo, cosi' si capisce perche' l'elenco e' diverso da quello di un altro account.
            var cost = string.IsNullOrEmpty(state) || string.Equals(state, "enabled", StringComparison.OrdinalIgnoreCase) ? CopilotCost(multiplier)
                : string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase) ? Loc.T("non abilitato per il tuo account o la tua organizzazione")
                : Loc.T("non ancora abilitato ({0})", state);
            models.Add(new AiModelInfo(id.Trim(), string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim(), cost));
        }

        return models;
    }

    /// <summary>Costo dichiarato da Copilot per una richiesta: moltiplicatore di richieste premium (0x = non consuma richieste premium). Vuoto se Copilot non lo dice.</summary>
    internal static string CopilotCost(double? multiplier)
        => multiplier is { } m and >= 0
            ? Loc.T("{0}× richieste premium per richiesta", m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
            : string.Empty;

    /// <summary>URL per elencare i modelli di un endpoint OpenAI-compatibile: toglie <c>/chat/completions</c> e aggiunge <c>/models</c>; GitHub Models ha il suo catalogo.</summary>
    internal static string ModelsUrlFor(string endpoint)
    {
        if (AiServiceFactory.IsGitHubModelsEndpoint(endpoint))
            return "https://models.github.ai/catalog/models";
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(Loc.T("L'endpoint AI \"{0}\" non è un URL valido.", endpoint));

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/completions" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }

        return new UriBuilder(uri) { Path = path + "/models", Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
