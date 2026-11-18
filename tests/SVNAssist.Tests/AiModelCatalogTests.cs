using System.Text.Json;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// Bug 8 (0.4.0): elenco modelli AI dal provider (non piu' enum fisse), cache datata, errore del modello ritirato con azione "Aggiorna elenco modelli".
/// I parser sono provati con JSON di esempio nel formato documentato dai provider: le risposte reali (Copilot, GitHub Models) non sono state verificate dal vivo.
/// </summary>
public class AiModelCatalogTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _dir = Directory.CreateTempSubdirectory("svnassist-models-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeSource : IAiModelSource
    {
        public int Calls;
        public int Forced;
        public Exception? Fail;
        public string Identity = "";
        public AiModelsFetched? Result = new([new AiModelInfo("m1", "Modello 1"), new AiModelInfo("m2", "Modello 2", "1× richieste premium per richiesta")], "Account GitHub: mrossi (accesso: gh-cli)");

        public Task<string> IdentityAsync(string provider, CancellationToken ct) => Task.FromResult(Identity);

        public Task<AiModelsFetched?> ListAsync(string provider, bool force, CancellationToken ct)
        {
            Calls++;
            if (force) Forced++;
            return Fail is null ? Task.FromResult(Result) : Task.FromException<AiModelsFetched?>(Fail);
        }
    }

    // ── Parser ──

    [Fact]
    public void ParseAnthropic_LeggeIdNomeEPaginazione()
    {
        var (models, hasMore, last) = AiModelCatalog.ParseAnthropicModels(
            """{"data":[{"type":"model","id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-01-01T00:00:00Z"},{"id":"x y","display_name":"id non valido"},{"id":"claude-x"}],"has_more":true,"first_id":"a","last_id":"claude-x"}""");

        Assert.Equal(["claude-sonnet-5-5", "claude-x"], models.Select(m => m.Id));
        Assert.Equal("Claude Sonnet 5.5", models[0].Label);
        Assert.Equal("claude-x", models[1].Label); // senza display_name: l'id
        Assert.True(hasMore);
        Assert.Equal("claude-x", last);
    }

    [Fact]
    public void ParseAnthropic_NonUnElenco_Lancia()
        => Assert.ThrowsAny<JsonException>(() => AiModelCatalog.ParseAnthropicModels("""{"error":{"type":"authentication_error"}}"""));

    [Fact]
    public void ParseGitHubModels_SaltaGliEmbedding_AccettaArrayEOggetto()
    {
        const string array = """[{"id":"openai/gpt-4.1","name":"OpenAI GPT-4.1","supported_output_modalities":["text"]},{"id":"openai/text-embedding-3-small","name":"Embedding","supported_output_modalities":["embeddings"]},{"id":"meta/Llama-3.3-70B-Instruct","name":"Llama 3.3"}]""";
        var models = AiModelCatalog.ParseGitHubModelsCatalog(array);
        Assert.Equal(["openai/gpt-4.1", "meta/Llama-3.3-70B-Instruct"], models.Select(m => m.Id));
        Assert.Equal(models.Select(m => m.Id), AiModelCatalog.ParseGitHubModelsCatalog("{\"data\":" + array + "}").Select(m => m.Id));
    }

    [Fact]
    public void ParseOpenAi_OrdinaEToglieEmbeddingEAudio()
    {
        var models = AiModelCatalog.ParseOpenAiModels("""{"object":"list","data":[{"id":"gpt-4o"},{"id":"text-embedding-3-large"},{"id":"gpt-4.1"},{"id":"whisper-1"},{"id":"gpt-4o"}]}""");
        Assert.Equal(["gpt-4.1", "gpt-4o"], models.Select(m => m.Id));
    }

    [Fact]
    public void CopilotModels_PredefinitoInTesta_NonAbilitatiVisibiliConIlMotivo_CostoDalMoltiplicatore()
    {
        var models = AiModelCatalog.BuildCopilotModels(
        [
            ("gpt-x", "GPT X", "enabled", 1.0),
            ("vecchio", "Vecchio", "disabled", 1.0),
            ("gratis", null, null, 0.0),
            ("senza-costo", "Senza costo", "enabled", null),
            ("da-accettare", "Da accettare", "unconfigured", 2.0),
        ]);

        Assert.Equal(["default", "gpt-x", "vecchio", "gratis", "senza-costo", "da-accettare"], models.Select(m => m.Id));
        Assert.Equal("1× richieste premium per richiesta", models[1].Cost);
        Assert.Contains("non abilitato", models[2].Cost); // visibile, con il motivo: capire perche' l'elenco e' diverso
        Assert.Equal("0× richieste premium per richiesta", models[3].Cost);
        Assert.Equal("gratis", models[3].Label);
        Assert.Equal(string.Empty, models[4].Cost); // mai un costo inventato
        Assert.Contains("unconfigured", models[5].Cost);
    }

    [Fact]
    public void CopilotModels_PredefinitoNominaIlModelloCheUsaDaSolo_SeIlRuntimeLoDice()
    {
        var raw = new (string, string?, string?, double?)[] { ("gpt-5.6", "GPT 5.6", "enabled", 1.0) };
        Assert.Equal("Predefinito di Copilot: GPT 5.6 (gpt-5.6)", AiModelCatalog.BuildCopilotModels(raw, "gpt-5.6")[0].Label);
        Assert.Equal("Predefinito di Copilot: ignoto-id", AiModelCatalog.BuildCopilotModels(raw, "ignoto-id")[0].Label);
        Assert.Contains("lo sceglie Copilot", AiModelCatalog.BuildCopilotModels(raw)[0].Label); // senza informazione: lo dice, non inventa
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/models")]
    [InlineData("https://models.github.ai/inference/chat/completions", "https://models.github.ai/catalog/models")]
    [InlineData("https://host/openai/v1/", "https://host/openai/v1/models")]
    public void ModelsUrl_DaEndpointChatCompletions(string endpoint, string expected)
        => Assert.Equal(expected, AiModelCatalog.ModelsUrlFor(endpoint));

    [Theory]
    [InlineData("gpt-4.1", true)]
    [InlineData("openai/gpt-4o-mini", true)]
    [InlineData("claude-sonnet-4.5", true)]
    [InlineData("", false)]
    [InlineData("due parole", false)]
    [InlineData("-x", false)]
    public void IdModello_Plausibile(string id, bool expected) => Assert.Equal(expected, AiModelIds.IsPlausible(id));

    // ── Cache ──

    [Fact]
    public async Task Cache_EntroLeVentiquattroOre_NonRichiamaIlProvider_ForzaturaSi()
    {
        var src = new FakeSource();
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var catalog = new AiModelCatalog(src, now: () => now);

        var first = await catalog.GetAsync("copilot", false, Ct);
        Assert.Equal("live", first.Source);
        Assert.Equal(now, first.UpdatedAt);
        Assert.Equal("Account GitHub: mrossi (accesso: gh-cli)", first.Account);

        now = now.AddHours(23);
        var second = await catalog.GetAsync("copilot", false, Ct);
        Assert.Equal("cache", second.Source);
        Assert.Equal(1, src.Calls);
        Assert.Equal(first.UpdatedAt, second.UpdatedAt);

        var forced = await catalog.GetAsync("copilot", true, Ct);
        Assert.Equal("live", forced.Source);
        Assert.Equal(2, src.Calls);
        Assert.Equal(1, src.Forced);
        Assert.Equal(now, forced.UpdatedAt);

        now = now.AddHours(25);
        Assert.Equal("live", (await catalog.GetAsync("copilot", false, Ct)).Source);
        Assert.Equal(3, src.Calls);
    }

    [Fact]
    public async Task Cache_MaxAgeDelChiamante_Rispettata()
    {
        var src = new FakeSource();
        var now = DateTimeOffset.UtcNow;
        var catalog = new AiModelCatalog(src, now: () => now);
        await catalog.GetAsync("claude", false, Ct);
        now = now.AddMinutes(90);

        Assert.Equal("cache", (await catalog.GetAsync("claude", false, Ct)).Source);
        Assert.Equal("live", (await catalog.GetAsync("claude", false, Ct, TimeSpan.FromHours(1))).Source);
    }

    [Fact]
    public async Task Errore_NonEntraInCache_SiRimandaLUltimaCopiaBuonaConLaSuaData()
    {
        var src = new FakeSource();
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var catalog = new AiModelCatalog(src, now: () => now);
        var good = await catalog.GetAsync("openai", false, Ct);

        src.Fail = new HttpRequestException("HTTP 500 boom");
        now = now.AddDays(2);
        var failed = await catalog.GetAsync("openai", false, Ct);

        Assert.Equal("cache", failed.Source);
        Assert.Equal(good.UpdatedAt, failed.UpdatedAt); // la data e' quella della copia, non "adesso"
        Assert.Contains("HTTP 500 boom", failed.Error);
        Assert.Equal(good.Models, failed.Models);

        src.Fail = null;
        Assert.Equal("live", (await catalog.GetAsync("openai", false, Ct)).Source); // l'errore non e' rimasto in cache
    }

    [Fact]
    public async Task Errore_SenzaCopia_ElencoPredefinitoConIlMotivo_NonRestaVuoto()
    {
        var src = new FakeSource { Fail = new InvalidOperationException("serve il login") };
        var list = await new AiModelCatalog(src).GetAsync("copilot", false, Ct);

        Assert.Equal("unlinked", list.Source);
        Assert.Equal("serve il login", list.Error);
        Assert.Null(list.UpdatedAt);
        Assert.Empty(list.Models);   // nessun id inventato
    }

    [Fact]
    public async Task ElencoVuotoDelProvider_EUnErrore_NonUnaCache()
    {
        var src = new FakeSource { Result = new AiModelsFetched([]) };
        var list = await new AiModelCatalog(src).GetAsync("claude", false, Ct);
        Assert.NotNull(list.Error);
        Assert.Equal("unlinked", list.Source);
    }

    [Fact]
    public async Task ProviderCheNonSiPuoElencare_NessunElencoSenzaErrore()
    {
        var src = new FakeSource { Result = null };
        var list = await new AiModelCatalog(src).GetAsync("claudecode", false, Ct);
        Assert.Equal("unlinked", list.Source);
        Assert.Null(list.Error);
        Assert.Empty(list.Models);
    }

    [Fact]
    public async Task Cache_SeparataPerEndpoint_EPersistenteSuFile()
    {
        var file = Path.Combine(_dir, "sub", "cache.json");
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var a = new FakeSource { Identity = "https://a/v1/chat/completions" };
        await new AiModelCatalog(a, file, () => now).GetAsync("openai", false, Ct);
        Assert.True(File.Exists(file));
        Assert.DoesNotContain("sk-", await File.ReadAllTextAsync(file)); // nessun segreto in cache

        // nuovo processo: la copia si rilegge dal file; un altro endpoint non la usa
        var again = new FakeSource { Identity = "https://a/v1/chat/completions" };
        var cached = await new AiModelCatalog(again, file, () => now.AddHours(1)).GetAsync("openai", false, Ct);
        Assert.Equal("cache", cached.Source);
        Assert.Equal(0, again.Calls);
        Assert.Equal("Account GitHub: mrossi (accesso: gh-cli)", cached.Account);
        Assert.Equal("1× richieste premium per richiesta", cached.Models[1].Cost);

        var other = new FakeSource { Identity = "https://b/v1/chat/completions" };
        Assert.Equal("live", (await new AiModelCatalog(other, file, () => now.AddHours(1)).GetAsync("openai", false, Ct)).Source);
    }

    [Fact]
    public async Task Cache_FileCorrotto_Ignorato()
    {
        var file = Path.Combine(_dir, "cache.json");
        await File.WriteAllTextAsync(file, "{non json");
        var src = new FakeSource();
        Assert.Equal("live", (await new AiModelCatalog(src, file).GetAsync("claude", false, Ct)).Source);
    }

    // ── Errore del modello -> messaggio chiaro + azione ──

    [Theory]
    [InlineData("AI API error 404 NotFound: {\"error\":{\"code\":\"model_not_found\",\"message\":\"The model `gpt-9` does not exist\"}}")]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-old\"}}")]
    [InlineData("Model \"claude-sonnet-4.5\" is not available for your plan")]
    [InlineData("unknown model gpt-5-mini")]
    [InlineData("AI API error 400 BadRequest: {\"error\":{\"message\":\"Unknown model: openai/foo\"}}")]
    [InlineData("The requested model is not supported.")]
    public void Classify_ModelloNonDisponibile(string message)
        => Assert.Equal(AiErrorKind.ModelUnavailable, AiErrors.Classify(message));

    [Theory]
    [InlineData("AI API error 401 Unauthorized: bad key")]
    [InlineData("Claude Code failed (exit code 1): Failed to authenticate: OAuth session expired and could not be refreshed")]
    [InlineData("GitHub Copilot: Copilot non è collegato: accedi a GitHub (gh auth login)")]
    [InlineData("invalid x-api-key")]
    public void Classify_AccessoScadutoOMancante(string message)
        => Assert.Equal(AiErrorKind.Auth, AiErrors.Classify(message));

    [Theory]
    [InlineData("Il provider AI non ha risposto entro 3 minuti: richiesta interrotta.")]
    [InlineData("rate limit exceeded")]
    [InlineData("")]
    public void Classify_AltriErroriRestanoErroriVeri(string message)
        => Assert.Equal(AiErrorKind.Other, AiErrors.Classify(message));

    [Fact]
    public void ProviderOf_DallaCatenaDiFallback_SceglieLaRigaDelModello()
    {
        const string msg = "Nessun provider AI ha risposto.\nClaude Code: processo terminato\nGitHub Copilot: Model \"x\" is not available";
        Assert.Equal("copilot", AiErrors.ProviderOf(msg));
        Assert.Null(AiErrors.ProviderOf("errore senza nome"));
    }

    private (CommitStore Vm, FakeAiFactory Ai) CreateVm()
    {
        var ai = new FakeAiFactory();
        var path = Path.Combine(_dir, "a.cs");
        return (new CommitStore(new StubSettings(), new FakeSvnService(), ai, new NoopAiPrompt(), () => [path], () => _dir), ai);
    }

    private sealed class ThrowingAi(Exception ex) : IAiService
    {
        public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null) => Task.FromException<string>(ex);

        public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null, string? style = null) => Task.FromException<string>(ex);
    }

    [Fact]
    public async Task GeneraConAi_ModelloRitirato_MessaggioChiaroConAzione_ErroreVeroVisibile()
    {
        var (vm, ai) = CreateVm();
        ai.Custom = new ThrowingAi(new InvalidOperationException("GitHub Copilot: Model \"gpt-5-mini\" is not available"));
        await vm.GenerateAsync(Ct);

        var snap = vm.GetSnapshot(null);
        Assert.StartsWith("Errore AI", snap.AiStatus);
        Assert.Equal("err", snap.AiLevel);
        Assert.Contains("gpt-5-mini", snap.AiStatus); // il dettaglio vero del provider resta
        Assert.Contains("modello", snap.AiStatus);
        Assert.Equal("wizardOpenModels", snap.AiAction!.Cmd);
        Assert.Equal("Aggiorna elenco modelli", snap.AiAction.Label);
        Assert.Equal("copilot", vm.FailedProvider);
    }

    private sealed class ModelReportingAi : IAiService, IReportsModel
    {
        public string? LastModelUsed => "gpt-5.6";

        public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null) => Task.FromResult("Titolo");

        public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null, string? style = null) => Task.FromResult("Titolo");
    }

    [Fact]
    public async Task GeneraConAi_MostraIlModelloUsatoDavvero_SeIlProviderLoRiporta()
    {
        var (vm, ai) = CreateVm();
        ai.Custom = new ModelReportingAi();
        await vm.GenerateAsync(Ct);
        Assert.Contains("modello gpt-5.6", vm.AiStatus);
    }

    [Fact]
    public async Task GeneraConAi_OpzioneModelloECosto_CostoNonDisponibileSeIlProviderNonLoDichiara()
    {
        var ai = new FakeAiFactory { Custom = new ModelReportingAi() };
        var path = Path.Combine(_dir, "a.cs");
        var vm = new CommitStore(new StubSettings { ShowUsage = true }, new FakeSvnService(), ai, new NoopAiPrompt(), () => [path], () => _dir);
        await vm.GenerateAsync(Ct);
        Assert.Contains("modello gpt-5.6", vm.AiStatus);
        Assert.Contains("costo non disponibile", vm.AiStatus);
    }

    [Fact]
    public async Task GeneraConAi_SenzaOpzione_NienteCosto()
    {
        var (vm, ai) = CreateVm();
        ai.Custom = new ModelReportingAi();
        await vm.GenerateAsync(Ct);
        Assert.DoesNotContain("costo", vm.AiStatus);
    }

    [Fact]
    public async Task GeneraConAi_ErroreDiAccesso_PulsanteRifaiLAccesso()
    {
        var (vm, ai) = CreateVm();
        ai.Custom = new ThrowingAi(new HttpRequestException("AI API error 401 Unauthorized: bad key"));
        await vm.GenerateAsync(Ct);

        var snap = vm.GetSnapshot(null);
        Assert.Contains("bad key", snap.AiStatus);
        Assert.Equal("wizardOpenAuth", snap.AiAction?.Cmd);
    }

    [Fact]
    public async Task GeneraConAi_ErroreQualsiasi_NienteAzione_MessaggioDelProvider()
    {
        var (vm, ai) = CreateVm();
        ai.Custom = new ThrowingAi(new HttpRequestException("rate limit exceeded"));
        await vm.GenerateAsync(Ct);

        var snap = vm.GetSnapshot(null);
        Assert.Equal("Errore AI: rate limit exceeded", snap.AiStatus);
        Assert.Null(snap.AiAction);
        Assert.DoesNotContain("aiAction", UiJson.Serialize(snap));
    }

    [Fact]
    public async Task NuovoMessaggio_TogliLAzione()
    {
        var (vm, ai) = CreateVm();
        ai.Custom = new ThrowingAi(new InvalidOperationException("unknown model foo"));
        await vm.GenerateAsync(Ct);
        Assert.NotNull(vm.GetSnapshot(null).AiAction);

        ai.Custom = null;
        await vm.GenerateAsync(Ct);
        Assert.Null(vm.GetSnapshot(null).AiAction);
    }

    // ── Procedura guidata: passo Modello con elenco del provider ──

    private (WizardStore Wizard, StubSettings Settings, FakeSource Source) CreateWizard(FakeSource? src = null, Action<StubSettings>? setup = null)
    {
        var settings = new StubSettings { AnthropicKey = "k" };
        setup?.Invoke(settings);
        var probes = new PrerequisiteProbes
        {
            FindGitHubCli = () => "gh",
            IsGitHubCliLoggedIn = (_, _) => Task.FromResult(true),
            ResolveGitHubToken = _ => Task.FromResult<string?>("tok"),
            CopilotRuntimeInstalled = () => true,
            FindClaude = () => "claude",
        };
        var prereq = new PrereqStore(new PrerequisitesService(settings, probes), new PrerequisiteActions());
        src ??= new FakeSource();
        return (new WizardStore(prereq, settings, new AiModelCatalog(src)), settings, src);
    }

    private static async Task ToStep4(WizardStore w, string provider, bool fromGenerate = false)
    {
        await w.OpenAsync(fromGenerate, Ct);
        w.Pick(provider);
        for (var i = 0; i < 3; i++)
            await w.NextAsync(Ct);
    }

    [Fact]
    public async Task Wizard_Passo4_ElencoDalProvider_ConCostoDataEAccount()
    {
        var (w, _, _) = CreateWizard();
        await ToStep4(w, "copilot");

        var s = w.GetSnapshot();
        Assert.Equal(4, s.Step);
        Assert.False(s.ModelsLoading);
        Assert.Equal(["m1", "m2"], s.Models.Select(m => m.Value));
        Assert.Equal("1× richieste premium per richiesta", s.Models[1].Cost);
        Assert.Equal("m1", s.Model); // nessun "consigliato": il primo dell'elenco, a meno che sia salvato un modello presente
        Assert.Equal("live", s.ModelsSource);
        Assert.NotEqual(string.Empty, s.ModelsUpdated);
        Assert.Contains("mrossi", s.ModelsAccount);
    }

    [Fact]
    public async Task Wizard_AggiornaElenco_ForzaIlProvider_ESeSparisceUnModelloLaSceltaSiAggiorna()
    {
        var (w, _, src) = CreateWizard(setup: s => s.ClaudeModelSaved = "m1");
        await ToStep4(w, "claude");
        Assert.Equal(1, src.Calls);
        Assert.Equal("m1", w.GetSnapshot().Model);

        src.Result = new AiModelsFetched([new AiModelInfo("nuovo", "Nuovo")]);
        await w.RefreshModelsAsync(Ct);

        var s = w.GetSnapshot();
        Assert.Equal(2, src.Calls);
        Assert.Equal(1, src.Forced);
        Assert.Equal(["nuovo", "m1"], s.Models.Select(m => m.Value)); // m1 e' salvato ma non piu' in elenco: resta, segnalato
        Assert.Contains("non più nell'elenco", s.Models[1].Label);
        Assert.Equal("nuovo", s.Model); // la scelta precedente non esiste piu': si passa al primo dell'elenco
    }

    [Fact]
    public async Task Wizard_ProviderInErrore_ElencoPredefinitoEMotivoVisibile_EsistePerComunqueAltro()
    {
        var (w, _, _) = CreateWizard(new FakeSource { Fail = new HttpRequestException("Elenco modelli non disponibile (HTTP 403): no") });
        await ToStep4(w, "copilot");

        var s = w.GetSnapshot();
        Assert.True(s.ModelsError);
        Assert.Contains("HTTP 403", s.ModelsNote);
        Assert.Empty(s.Models);
        Assert.Equal("unlinked", s.ModelsSource);
        Assert.Equal(string.Empty, s.ModelsUpdated);

        // Con "Altro..." si salva qualsiasi id valido: non si resta bloccati
        await w.FinishAsync("modello-aziendale-5.6", Ct);
        Assert.False(w.GetSnapshot().Open);
    }

    [Fact]
    public async Task Wizard_ValoreSalvatoNonPiuInElenco_RestaSegnalatoENonScartato()
    {
        var (w, _, _) = CreateWizard(setup: s => s.CopilotModelSaved = "gpt-5-mini");
        await ToStep4(w, "copilot");

        var s = w.GetSnapshot();
        Assert.Contains(s.Models, m => m.Value == "gpt-5-mini" && m.Label.Contains("non più nell'elenco"));
        Assert.Contains("gpt-5-mini", s.ModelsNote);
        Assert.Equal("gpt-5-mini", s.ModelSaved);
        Assert.Equal("m1", s.Model); // si propone un modello dell'elenco, ma quello salvato resta in uso finche' non si salva altro
    }

    [Fact]
    public async Task Wizard_ValoreSalvatoPresente_ESelezionato()
    {
        var (w, _, _) = CreateWizard(setup: s => s.CopilotModelSaved = "m2");
        await ToStep4(w, "copilot");
        Assert.Equal("m2", w.GetSnapshot().Model);
        Assert.DoesNotContain("non più", w.GetSnapshot().ModelsNote);
    }

    [Fact]
    public async Task Wizard_ClaudeCodeSenzaChiave_ElencoPredefinitoConSpiegazione()
    {
        var (w, _, _) = CreateWizard(new FakeSource { Result = null });
        await ToStep4(w, "claudecode");
        var s = w.GetSnapshot();
        Assert.Equal("unlinked", s.ModelsSource);
        Assert.False(s.ModelsError);
        Assert.Contains("API key Anthropic", s.ModelsNote);
    }

    [Theory]
    [InlineData("due parole")]
    [InlineData("x;rm")]
    public async Task Wizard_Fine_RifiutaIdNonValido(string model)
    {
        var (w, settings, _) = CreateWizard();
        await ToStep4(w, "claude");
        await Assert.ThrowsAsync<ArgumentException>(() => w.FinishAsync(model, Ct));
        Assert.Empty(settings.Saved);
    }

    [Fact]
    public async Task Wizard_OpenModels_ApreDirettamenteIlPassoModelloConElencoForzato_FineRigenera()
    {
        var (w, settings, src) = CreateWizard();
        await w.OpenModelsAsync("copilot", Ct);

        var s = w.GetSnapshot();
        Assert.True(s.Open);
        Assert.Equal(4, s.Step);
        Assert.Equal("copilot", s.Provider);
        Assert.True(s.FromGenerate);
        Assert.Equal(1, src.Forced);
        Assert.Equal(["m1", "m2"], s.Models.Select(m => m.Value));

        Assert.True(await w.FinishAsync("m2", Ct));
        Assert.Equal("m2", settings.Saved[SettingDefinitions.CopilotModel]);
    }

    [Fact]
    public async Task Wizard_OpenModels_SenzaProviderNoto_PartiDalPasso1()
    {
        var (w, _, _) = CreateWizard();
        await w.OpenModelsAsync(null, Ct); // provider "auto" nelle impostazioni
        Assert.Equal(1, w.GetSnapshot().Step);
    }

    [Fact]
    public async Task ChangesStore_ComandiModelli_Funzionano()
    {
        var settings = new StubSettings();
        var wizard = new WizardStore(new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), settings, new AiModelCatalog(new FakeSource()));
        var store = new ChangesStore(settings, new FakeSvnService(), new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
            new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())), wizard);
        Task Send(string name) => store.DispatchAsync(name, JsonSerializer.SerializeToElement(new { }), Ct);

        await Send("wizardModelsRefresh"); // senza procedura aperta: ignorato
        Assert.False(wizard.GetSnapshot().Open);
        await Send("wizardOpenModels");    // nessun provider noto: parte dal passo 1
        Assert.True(wizard.GetSnapshot().Open);
        Assert.Equal(1, wizard.GetSnapshot().Step);
    }
}
