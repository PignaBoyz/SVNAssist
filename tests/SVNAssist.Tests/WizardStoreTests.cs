using System.Text.Json;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Procedura guidata dell'AI: scelta provider, prerequisiti filtrati, passi abilitati, salvataggio.</summary>
public class WizardStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Env
    {
        public bool Gh, GhLogin, Runtime, Claude;
    }

    private static (WizardStore Wizard, StubSettings Settings, List<ConsoleCommand> Launched, Env Env) Create(Env? env = null)
    {
        env ??= new Env();
        var settings = new StubSettings();
        var launched = new List<ConsoleCommand>();
        var probes = new PrerequisiteProbes
        {
            FindGitHubCli = () => env.Gh ? "gh" : null,
            IsGitHubCliLoggedIn = (_, _) => Task.FromResult(env.GhLogin),
            ResolveGitHubToken = _ => Task.FromResult(env.GhLogin ? "tok" : null),
            CopilotRuntimeInstalled = () => env.Runtime,
            FindClaude = () => env.Claude ? "claude" : null,
        };
        var actions = new PrerequisiteActions { LaunchConsole = (c, _) => { launched.Add(c); return Task.FromResult(true); } };
        var prereq = new PrereqStore(new PrerequisitesService(settings, probes), actions);
        return (new WizardStore(prereq, settings), settings, launched, env);
    }

    [Fact]
    public async Task Copilot_ProviderPoiPrerequisitiFiltrati_AvantiSiAbilitaQuandoSonoOk()
    {
        var (wizard, _, _, env) = Create();
        await wizard.OpenAsync(fromGenerate: true, Ct);

        var s = wizard.GetSnapshot();
        Assert.True(s.Open);
        Assert.Equal(1, s.Step);
        Assert.False(s.CanNext);
        Assert.Equal(4, s.Providers.Count);

        wizard.Pick("copilot");
        Assert.True(wizard.GetSnapshot().CanNext);
        await wizard.NextAsync(Ct);

        s = wizard.GetSnapshot();
        Assert.Equal(2, s.Step);
        Assert.Equal([PrerequisiteIds.GitHubCli, PrerequisiteIds.CopilotRuntime], s.Items.Select(i => i.Id));
        Assert.False(s.CanNext);

        await wizard.NextAsync(Ct);
        Assert.Equal(2, wizard.GetSnapshot().Step);

        env.Gh = true;
        env.Runtime = true;
        await wizard.RefreshAsync(Ct);
        Assert.True(wizard.GetSnapshot().CanNext);
        await wizard.NextAsync(Ct);
        Assert.Equal(3, wizard.GetSnapshot().Step);
    }

    [Fact]
    public async Task Copilot_Accesso_RichiedeIlLoginGitHub()
    {
        var (wizard, _, launched, env) = Create(new Env { Gh = true, Runtime = true });
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("copilot");
        await wizard.NextAsync(Ct);
        await wizard.NextAsync(Ct);

        var s = wizard.GetSnapshot();
        Assert.Equal(3, s.Step);
        Assert.Equal(WizardStore.AuthGitHub, s.Auth);
        Assert.False(s.AuthOk);
        Assert.False(s.CanNext);

        await wizard.LoginAsync(Ct);
        Assert.Equal([ConsoleCommand.GitHubLogin], launched);

        env.GhLogin = true;
        await wizard.RefreshAsync(Ct);
        Assert.True(wizard.GetSnapshot().AuthOk);
        Assert.True(wizard.GetSnapshot().CanNext);
    }

    [Fact]
    public async Task Accesso_SeGhManca_LanciaPrimaLInstallazione()
    {
        var (wizard, _, launched, _) = Create();
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("openai");
        await wizard.NextAsync(Ct);
        await wizard.NextAsync(Ct);
        await wizard.LoginAsync(Ct);

        Assert.Equal([ConsoleCommand.InstallGitHubCli], launched);
    }

    [Fact]
    public async Task Prerequisito_UsaLeAzioniDellaChecklist_EUnIdEstraneoVieneRifiutato()
    {
        var (wizard, _, launched, _) = Create();
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("claudecode");
        await wizard.NextAsync(Ct);

        Assert.Equal([PrerequisiteIds.ClaudeCode], wizard.GetSnapshot().Items.Select(i => i.Id));
        await wizard.RunAsync(PrerequisiteIds.ClaudeCode, true, Ct);
        Assert.Equal([ConsoleCommand.InstallClaudeCode], launched);

        await Assert.ThrowsAsync<ArgumentException>(() => wizard.RunAsync(PrerequisiteIds.Svn, true, Ct));
        Assert.Single(launched);
        Assert.False(wizard.GetSnapshot().Busy);
    }

    [Fact]
    public async Task ClaudeCode_IlLoginNonSiVerifica_AvantiRestaAbilitato()
    {
        var (wizard, _, _, _) = Create(new Env { Claude = true });
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("claudecode");
        await wizard.NextAsync(Ct);
        Assert.True(wizard.GetSnapshot().CanNext);
        await wizard.NextAsync(Ct);

        var s = wizard.GetSnapshot();
        Assert.Equal(WizardStore.AuthClaudeCode, s.Auth);
        Assert.True(s.CanNext);
    }

    [Fact]
    public async Task ClaudeApi_ChiaveSalvataENonEsposta()
    {
        var (wizard, settings, _, _) = Create();
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("claude");
        await wizard.NextAsync(Ct);
        Assert.Empty(wizard.GetSnapshot().Items);
        Assert.True(wizard.GetSnapshot().CanNext);
        await wizard.NextAsync(Ct);
        Assert.False(wizard.GetSnapshot().CanNext);

        await wizard.SetAuthAsync("", "sk-ant-segreta", Ct);

        Assert.Equal("sk-ant-segreta", settings.Saved[SettingDefinitions.AnthropicApiKey]);
        var s = wizard.GetSnapshot();
        Assert.True(s.AuthOk);
        Assert.DoesNotContain("sk-ant-segreta", UiJson.Serialize(s));
    }

    [Theory]
    [InlineData("file:///c:/x")]
    [InlineData("http://example.com/v1")]
    [InlineData("non-un-url")]
    public async Task OpenAi_EndpointNonSicuro_Rifiutato(string endpoint)
    {
        var (wizard, settings, _, _) = Create();
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("openai");

        await Assert.ThrowsAsync<ArgumentException>(() => wizard.SetAuthAsync(endpoint, "k", Ct));
        Assert.Empty(settings.Saved);
    }

    [Fact]
    public async Task OpenAi_EndpointLocaleEChiave_Accettati()
    {
        var (wizard, settings, _, _) = Create();
        await wizard.OpenAsync(false, Ct);
        wizard.Pick("openai");
        await wizard.SetAuthAsync("http://localhost:11434/v1/chat/completions", "k", Ct);

        Assert.Equal("http://localhost:11434/v1/chat/completions", settings.Saved[SettingDefinitions.AiEndpoint]);
        Assert.Equal("k", settings.Saved[SettingDefinitions.AiApiKey]);
        Assert.True(wizard.GetSnapshot().AuthOk);
    }

    [Theory]
    [InlineData("copilot", SettingDefinitions.CopilotModel, "default")]
    [InlineData("claudecode", SettingDefinitions.ClaudeModel, "claude-sonnet-5-5")]
    [InlineData("claude", SettingDefinitions.ClaudeModel, "claude-sonnet-5-5")]
    [InlineData("openai", SettingDefinitions.AiModel, "openai/gpt-4o-mini")]
    public async Task Fine_SalvaProviderEModelloConLeChiaviDiOptions(string provider, string modelKey, string recommended)
    {
        var (wizard, settings, _, _) = Create(new Env { Gh = true, GhLogin = true, Runtime = true, Claude = true });
        settings.AnthropicKey = "k";
        await wizard.OpenAsync(fromGenerate: true, Ct);
        wizard.Pick(provider);
        for (var i = 0; i < 3; i++)
            await wizard.NextAsync(Ct);

        var s = wizard.GetSnapshot();
        Assert.Equal(4, s.Step);
        Assert.Equal(recommended, s.Model);
        Assert.Equal(recommended, s.Models[0].Value);

        Assert.True(await wizard.FinishAsync(recommended, Ct));
        Assert.Equal(provider, settings.Saved[SettingDefinitions.AiProvider]);
        Assert.Equal(recommended, settings.Saved[modelKey]);
        Assert.False(wizard.GetSnapshot().Open);
    }

    [Fact]
    public async Task Fine_ModelloSconosciuto_Rifiutato_EDaMenuNonGenera()
    {
        var (wizard, settings, _, _) = Create(new Env { Gh = true, GhLogin = true, Runtime = true });
        await wizard.OpenAsync(fromGenerate: false, Ct);
        wizard.Pick("copilot");
        for (var i = 0; i < 3; i++)
            await wizard.NextAsync(Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => wizard.FinishAsync("modello-inventato", Ct));
        Assert.Empty(settings.Saved);
        Assert.False(await wizard.FinishAsync("gpt-4.1", Ct));
    }

    [Fact]
    public async Task IndietroEAnnulla_ProviderPreselezionatoDalleImpostazioni()
    {
        var (wizard, settings, _, _) = Create();
        settings.Provider = "claude";
        await wizard.OpenAsync(false, Ct);
        Assert.Equal("claude", wizard.GetSnapshot().Provider);

        await wizard.NextAsync(Ct);
        wizard.Back();
        Assert.Equal(1, wizard.GetSnapshot().Step);
        wizard.Back();
        Assert.Equal(1, wizard.GetSnapshot().Step);

        wizard.Cancel();
        Assert.False(wizard.GetSnapshot().Open);
        Assert.Empty(settings.Saved);
    }

    [Fact]
    public async Task Prerequisiti_ProceduraSuSvnEProviderAi_ELaRigaAiPortaAllaProceduraAi()
    {
        var (wizard, _, launched, _) = Create();
        await wizard.OpenPrereqAsync(Ct);

        var s = wizard.GetSnapshot();
        Assert.True(s.Open);
        Assert.Equal(WizardStore.ModePrereq, s.Mode);
        Assert.Equal([PrerequisiteIds.Svn, WizardStore.AiRowId], s.Items.Select(i => i.Id));
        Assert.All(s.Items, i => Assert.False(i.Ok));
        Assert.False(s.CanNext);

        await wizard.RunAsync(PrerequisiteIds.Svn, true, Ct);
        Assert.Equal([ConsoleCommand.InstallSvn], launched);

        await wizard.RunAsync(WizardStore.AiRowId, true, Ct);
        s = wizard.GetSnapshot();
        Assert.Equal(WizardStore.ModeAi, s.Mode);
        Assert.Equal(1, s.Step);
    }

    [Fact]
    public async Task OpenOptions_ChiudeLaProceduraEApreLeOpzioniDellHost()
    {
        var settings = new StubSettings();
        var host = new FakeHost();
        var store = new ChangesStore(settings, new FakeSvnService(), new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), host,
            new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())));
        await store.DispatchAsync("wizardOpen", JsonSerializer.SerializeToElement(new { }), Ct);
        await store.DispatchAsync("openOptions", JsonSerializer.SerializeToElement(new { }), Ct);

        Assert.Equal(1, host.SettingsOpened);
        Assert.False(store.Wizard.GetSnapshot().Open);
    }

    [Theory]
    [InlineData("^/trunk", "trunk")]
    [InlineData("^/branches/feature%20x", "branches/feature x")]
    [InlineData("^/", "/ (radice)")]
    [InlineData("", "/ (radice)")]
    public void Branch_ConRadiceDelRepository_NonDiventaUnknown(string relativeUrl, string expected)
        => Assert.Equal(expected, SvnService.BranchLabel(relativeUrl));

    [Theory]
    [InlineData("", false, "")]
    [InlineData("Errore AI: boom", false, "err")]
    [InlineData("Metti almeno un file in Staged per generare il messaggio.", false, "warn")]
    [InlineData("Nessun provider AI configurato", false, "warn")]
    [InlineData("Messaggio generato con AI (Claude).", false, "ok")]
    [InlineData("Generazione in corso...", true, "busy")]
    public void LivelloDelMessaggioAi_PerColoreEIcona(string status, bool busy, string expected)
        => Assert.Equal(expected, CommitStore.LevelOf(status, busy));

    [Fact]
    public void ProviderSconosciuto_Rifiutato()
    {
        var (wizard, _, _, _) = Create();
        Assert.Throws<ArgumentException>(() => wizard.Pick("auto"));
    }

    [Fact]
    public void ModelliDellaProcedura_CoincidonoConQuelliAmmessiDalleImpostazioni()
    {
        Assert.Equal(SettingDefinitions.KnownClaudeModels, WizardStore.ModelsFor("claude").Select(m => m.Value));
        Assert.Equal(SettingDefinitions.KnownCopilotModels, WizardStore.ModelsFor("copilot").Select(m => m.Value));
        Assert.Equal(SettingDefinitions.KnownAiModels, WizardStore.ModelsFor("openai").Select(m => m.Value));
    }

    [Fact]
    public async Task ChangesStore_ComandiWizard_FinoAlSalvataggio()
    {
        var settings = new StubSettings();
        var store = new ChangesStore(settings, new FakeSvnService(), new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
            new PrereqStore(new PrerequisitesService(settings, new PrerequisiteProbes())));
        Task Send(string name, object args) => store.DispatchAsync(name, JsonSerializer.SerializeToElement(args), Ct);

        await Send("wizardOpen", new { });
        Assert.True(store.Wizard.GetSnapshot().Open);
        await Send("wizardPick", new { provider = "claude" });
        await Send("wizardNext", new { });
        await Send("wizardNext", new { });
        await Send("wizardSetAuth", new { endpoint = "", key = "sk" });
        await Send("wizardNext", new { });
        await Send("wizardFinish", new { model = "claude-haiku-5-5" });

        Assert.Equal("claude", settings.Saved[SettingDefinitions.AiProvider]);
        Assert.False(store.Wizard.GetSnapshot().Open);
    }
}
