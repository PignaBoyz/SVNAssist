using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Checklist dei prerequisiti, comandi della console e relativo store.</summary>
public class PrerequisitesTests
{
    private static PrerequisitesService Service(StubSettings settings, PrerequisiteProbes? probes = null)
        => new(settings, probes ?? new PrerequisiteProbes());

    private static PrerequisiteItem Item(PrerequisitesSnapshot s, string id) => s.Items.Single(i => i.Id == id);

    [Fact]
    public async Task SvnMancante_E_EssenzialeEContaComeProblema()
    {
        var snapshot = await Service(new StubSettings()).CheckAsync(CancellationToken.None);

        Assert.True(snapshot.SvnMissing);
        Assert.False(Item(snapshot, PrerequisiteIds.Svn).IsOk);
        Assert.Equal("Installa con winget", Item(snapshot, PrerequisiteIds.Svn).PrimaryAction);
        // svn mancante + nessun provider AI.
        Assert.Equal(2, snapshot.ProblemCount);
    }

    [Fact]
    public async Task SvnPresenteEClaudeCode_NessunProblema()
    {
        var probes = new PrerequisiteProbes { FindSvn = () => @"C:\svn.exe", FindClaude = () => @"C:\claude.exe" };

        var snapshot = await Service(new StubSettings(), probes).CheckAsync(CancellationToken.None);

        Assert.False(snapshot.SvnMissing);
        Assert.Equal(["Claude Code"], snapshot.UsableProviders);
        Assert.Equal(0, snapshot.ProblemCount);
    }

    [Fact]
    public async Task TortoiseMancante_NonContaComeProblema()
    {
        var probes = new PrerequisiteProbes { FindSvn = () => "svn", FindClaude = () => "claude" };

        var snapshot = await Service(new StubSettings(), probes).CheckAsync(CancellationToken.None);

        Assert.False(Item(snapshot, PrerequisiteIds.TortoiseSvn).IsOk);
        Assert.Equal(0, snapshot.ProblemCount);
    }

    [Fact]
    public async Task ProviderImpostato_LimitaQuelliUtilizzabili()
    {
        var probes = new PrerequisiteProbes { FindClaude = () => "claude", ReadEnvironment = _ => "sk-ant" };

        var snapshot = await Service(new StubSettings { Provider = "claude" }, probes).CheckAsync(CancellationToken.None);

        Assert.Equal(["Claude API"], snapshot.UsableProviders);
        Assert.Equal("claude", snapshot.ConfiguredProvider);
    }

    [Fact]
    public async Task Copilot_InAuto_RichiedeRuntimeETokenMaEsplicitoBasta()
    {
        var probes = new PrerequisiteProbes { CopilotRuntimeInstalled = () => false, ResolveGitHubToken = _ => Task.FromResult<string?>("t") };

        var auto = await Service(new StubSettings(), probes).CheckAsync(CancellationToken.None);
        var explicitCopilot = await Service(new StubSettings { Provider = "copilot" }, probes).CheckAsync(CancellationToken.None);

        Assert.DoesNotContain("GitHub Copilot", auto.UsableProviders);
        Assert.Contains("GitHub Copilot", explicitCopilot.UsableProviders);
    }

    [Fact]
    public async Task Copilot_SenzaLogin_NonEUtilizzabileNemmenoSeEsplicito()
    {
        var probes = new PrerequisiteProbes { CopilotRuntimeInstalled = () => true };

        var snapshot = await Service(new StubSettings { Provider = "copilot" }, probes).CheckAsync(CancellationToken.None);

        Assert.DoesNotContain("GitHub Copilot", snapshot.UsableProviders);
        Assert.Equal(1, snapshot.ProblemCount - (snapshot.SvnMissing ? 1 : 0));
    }

    [Fact]
    public async Task ProviderFisso_MostraSoloIPrerequisitiAiRilevanti()
    {
        var claude = await Service(new StubSettings { Provider = "claudecode" }).CheckAsync(CancellationToken.None);
        var copilot = await Service(new StubSettings { Provider = "copilot" }).CheckAsync(CancellationToken.None);
        var auto = await Service(new StubSettings()).CheckAsync(CancellationToken.None);

        Assert.DoesNotContain(claude.Items, i => i.Id is PrerequisiteIds.GitHubCli or PrerequisiteIds.AnthropicKey or PrerequisiteIds.CopilotRuntime);
        Assert.Contains(claude.Items, i => i.Id == PrerequisiteIds.ClaudeCode);
        Assert.Contains("abbonamento Claude", claude.ProviderNote);
        Assert.Contains(copilot.Items, i => i.Id == PrerequisiteIds.GitHubLogin);
        Assert.DoesNotContain(copilot.Items, i => i.Id == PrerequisiteIds.ClaudeCode);
        Assert.Contains(auto.Items, i => i.Id == PrerequisiteIds.ClaudeCode);
        Assert.Contains(auto.Items, i => i.Id == PrerequisiteIds.OpenAiKey);
    }

    [Theory]
    [InlineData("copilot", "https://github.com/features/copilot")]
    [InlineData("claudecode", PrerequisitesService.ClaudeCodeDocsUrl)]
    [InlineData("claude", AiProviderGuide.AnthropicKeysUrl)]
    [InlineData("openai", AiProviderGuide.GitHubTokenUrl)]
    [InlineData("auto", AiProviderGuide.GitHubTokenUrl)]
    public void AiProviderGuide_PaginaDiConfigurazioneDelProviderScelto(string provider, string expected)
        => Assert.Equal(expected, AiProviderGuide.SetupUrl(provider));

    [Fact]
    public async Task GitHubModels_ConTokenDaGh_RendeUtilizzabileOpenAiCompatibile()
    {
        var probes = new PrerequisiteProbes { ResolveGitHubToken = _ => Task.FromResult<string?>("ghp") };

        var snapshot = await Service(new StubSettings(), probes).CheckAsync(CancellationToken.None);

        Assert.Equal(["OpenAI-compatibile"], snapshot.UsableProviders);
    }

    [Fact]
    public async Task AccediAGitHub_SoloSeGhInstallatoMaNonLoggato()
    {
        var noGh = await Service(new StubSettings()).CheckAsync(CancellationToken.None);
        var notLogged = await Service(new StubSettings(), new PrerequisiteProbes { FindGitHubCli = () => "gh" }).CheckAsync(CancellationToken.None);
        var logged = await Service(new StubSettings(), new PrerequisiteProbes
        {
            FindGitHubCli = () => "gh",
            IsGitHubCliLoggedIn = (_, _) => Task.FromResult(true),
        }).CheckAsync(CancellationToken.None);

        Assert.Equal("Installa GitHub CLI", Item(noGh, PrerequisiteIds.GitHubCli).PrimaryAction);
        Assert.Null(Item(noGh, PrerequisiteIds.GitHubLogin).PrimaryAction);
        Assert.Equal("Accedi a GitHub", Item(notLogged, PrerequisiteIds.GitHubLogin).PrimaryAction);
        Assert.Null(Item(logged, PrerequisiteIds.GitHubLogin).PrimaryAction);
        Assert.True(Item(logged, PrerequisiteIds.GitHubLogin).IsOk);
    }

    [Fact]
    public async Task ClaudeCode_InstallaOAccedi_SecondoSeTrovato()
    {
        var missing = await Service(new StubSettings()).CheckAsync(CancellationToken.None);
        var found = await Service(new StubSettings(), new PrerequisiteProbes { FindClaude = () => "claude" }).CheckAsync(CancellationToken.None);

        Assert.Equal("Installa Claude Code", Item(missing, PrerequisiteIds.ClaudeCode).PrimaryAction);
        Assert.Equal("Accedi a Claude Code", Item(found, PrerequisiteIds.ClaudeCode).PrimaryAction);
    }

    [Fact]
    public async Task AnthropicKey_DaImpostazioniODaVariabile()
    {
        var fromSettings = await Service(new StubSettings { AnthropicKey = "k" }).CheckAsync(CancellationToken.None);
        var fromEnv = await Service(new StubSettings(), new PrerequisiteProbes { ReadEnvironment = _ => "k" }).CheckAsync(CancellationToken.None);
        var none = await Service(new StubSettings()).CheckAsync(CancellationToken.None);

        Assert.True(Item(fromSettings, PrerequisiteIds.AnthropicKey).IsOk);
        Assert.True(Item(fromEnv, PrerequisiteIds.AnthropicKey).IsOk);
        Assert.False(Item(none, PrerequisiteIds.AnthropicKey).IsOk);
    }

    // ── ConsoleLauncher ──────────────────────────────────────────────

    [Fact]
    public void ConsoleLauncher_ComandiCostruitiDaCostanti()
    {
        Assert.Equal(("cmd.exe", "/k gh auth login --hostname github.com --git-protocol https --web"),
            ConsoleLauncher.Build(ConsoleCommand.GitHubLogin));
        Assert.Equal(("cmd.exe", "/k winget install --id GitHub.cli -e --accept-package-agreements --accept-source-agreements"),
            ConsoleLauncher.Build(ConsoleCommand.InstallGitHubCli));
        Assert.Equal(("cmd.exe", "/k winget install --id Slik.Subversion -e --accept-package-agreements --accept-source-agreements"),
            ConsoleLauncher.Build(ConsoleCommand.InstallSvn));
        Assert.Equal(("powershell.exe", "-NoExit -Command \"irm https://claude.ai/install.ps1 | iex\""),
            ConsoleLauncher.Build(ConsoleCommand.InstallClaudeCode));
    }

    [Fact]
    public void ConsoleLauncher_ClaudeLogin_UsaIlPercorsoTrovatoTraVirgolette()
    {
        Assert.Equal(("cmd.exe", "/k claude"), ConsoleLauncher.Build(ConsoleCommand.ClaudeLogin));
        Assert.Equal(("cmd.exe", "/k \"\"C:\\Users\\me\\.local\\bin\\claude.exe\"\""),
            ConsoleLauncher.Build(ConsoleCommand.ClaudeLogin, @"C:\Users\me\.local\bin\claude.exe"));
    }

    [Fact]
    public void ConsoleLauncher_ShellCommand_SintassiPowerShellSenzaCmdK()
    {
        Assert.Equal("gh auth login --hostname github.com --git-protocol https --web", ConsoleLauncher.ShellCommand(ConsoleCommand.GitHubLogin));
        Assert.Equal("irm https://claude.ai/install.ps1 | iex", ConsoleLauncher.ShellCommand(ConsoleCommand.InstallClaudeCode));
        Assert.Equal("claude", ConsoleLauncher.ShellCommand(ConsoleCommand.ClaudeLogin));
        Assert.Equal("& \"C:\\Users\\me\\.local\\bin\\claude.exe\"",
            ConsoleLauncher.ShellCommand(ConsoleCommand.ClaudeLogin, @"C:\Users\me\.local\bin\claude.exe"));
    }

    // ── Store ────────────────────────────────────────────────────────

    private static (PrereqStore Store, List<ConsoleCommand> Launched, List<string> Urls) CreateStore(PrerequisiteProbes probes)
    {
        var launched = new List<ConsoleCommand>();
        var urls = new List<string>();
        var actions = new PrerequisiteActions
        {
            LaunchConsole = (c, _) => { launched.Add(c); return Task.FromResult(true); },
            OpenUrl = u => { urls.Add(u); return Task.CompletedTask; },
            DownloadCopilotRuntime = _ => Task.CompletedTask,
        };
        return (new PrereqStore(Service(new StubSettings(), probes), actions), launched, urls);
    }

    [Fact]
    public async Task Store_ApreLaChecklistDaSolaSeManca_Svn()
    {
        var (store, _, _) = CreateStore(new PrerequisiteProbes());

        await store.InitializeAsync(CancellationToken.None);

        Assert.True(store.IsOpen);
        Assert.Contains("da sistemare", store.GetSnapshot().Summary);
    }

    [Fact]
    public async Task Store_NonApreLaChecklistSeSvnCe()
    {
        var (store, _, _) = CreateStore(new PrerequisiteProbes { FindSvn = () => "svn", FindClaude = () => "c" });

        await store.InitializeAsync(CancellationToken.None);

        Assert.False(store.IsOpen);
        Assert.Equal("Prerequisiti ✓", store.GetSnapshot().Summary);
    }

    [Fact]
    public async Task Store_PulsantePrincipaleDiGitHubLogin_LanciaIlTerminale()
    {
        var (store, launched, _) = CreateStore(new PrerequisiteProbes { FindGitHubCli = () => "gh" });
        await store.InitializeAsync(CancellationToken.None);

        await store.RunActionAsync(PrerequisiteIds.GitHubLogin, primary: true, CancellationToken.None);

        Assert.Equal([ConsoleCommand.GitHubLogin], launched);
        Assert.Contains("Ricontrolla", store.Feedback);
    }

    [Fact]
    public async Task Store_SecondaryDiSvn_ApreLaPaginaDiDownload()
    {
        var (store, _, urls) = CreateStore(new PrerequisiteProbes());
        await store.InitializeAsync(CancellationToken.None);

        await store.RunActionAsync(PrerequisiteIds.Svn, primary: false, CancellationToken.None);

        Assert.Equal([SvnEnvironment.DownloadUrl], urls);
    }

    [Fact]
    public async Task Store_IdSconosciuto_Rifiutato()
    {
        var (store, launched, urls) = CreateStore(new PrerequisiteProbes());
        await store.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => store.RunActionAsync("../../evil", primary: true, CancellationToken.None));

        Assert.Empty(launched);
        Assert.Empty(urls);
    }

    [Fact]
    public async Task Store_QuandoSvnDiventaDisponibile_NotificaIlPannello()
    {
        var found = false;
        var probes = new PrerequisiteProbes { FindSvn = () => found ? "svn" : null };
        var (store, _, _) = CreateStore(probes);
        var notified = 0;
        store.SvnBecameAvailable = _ => { notified++; return Task.CompletedTask; };

        await store.InitializeAsync(CancellationToken.None);
        Assert.Equal(0, notified);

        found = true;
        await store.RefreshAsync(CancellationToken.None);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task Store_OgniElementoHaIlProprioGruppo()
    {
        var (store, _, _) = CreateStore(new PrerequisiteProbes());
        await store.InitializeAsync(CancellationToken.None);

        var groups = store.GetSnapshot().Items.Select(i => i.Group).Distinct().ToList();

        Assert.Equal(["Obbligatori", "Opzionali", "Provider AI (ne basta uno)"], groups);
    }

    // ── ReloadableSvnService ─────────────────────────────────────────

    [Fact]
    public async Task ReloadableSvnService_RitrovaSvnInstallatoDopo()
    {
        string? path = null;
        var created = new List<string>();
        var inner = new FakeSvnService();
        var service = new ReloadableSvnService(() => path, p => { created.Add(p); return inner; });

        await service.GetBranchNameAsync("wc", CancellationToken.None);
        path = @"C:\svn.exe";
        await service.GetBranchNameAsync("wc", CancellationToken.None);
        await service.GetBranchNameAsync("wc", CancellationToken.None);

        Assert.Equal(["svn.exe", @"C:\svn.exe"], created);
    }
}
