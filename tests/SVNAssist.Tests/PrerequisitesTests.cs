using SVNAssist.Services;
using SVNAssist.ToolWindows;

namespace SVNAssist.Tests;

/// <summary>Checklist dei prerequisiti, comandi della console e relativo ViewModel.</summary>
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

    // ── ViewModel ────────────────────────────────────────────────────

    private static (PrerequisitesViewModel Vm, List<ConsoleCommand> Launched, List<string> Urls) CreateVm(PrerequisiteProbes probes)
    {
        var launched = new List<ConsoleCommand>();
        var urls = new List<string>();
        var actions = new PrerequisiteActions
        {
            LaunchConsole = (c, _) => { launched.Add(c); return true; },
            OpenUrl = urls.Add,
            DownloadCopilotRuntime = _ => Task.CompletedTask,
        };
        return (new PrerequisitesViewModel(Service(new StubSettings(), probes), actions), launched, urls);
    }

    [Fact]
    public async Task ViewModel_ApreLaChecklistDaSolaSeManca_Svn()
    {
        var (vm, _, _) = CreateVm(new PrerequisiteProbes());

        await vm.InitializeAsync(CancellationToken.None);

        Assert.True(vm.IsOpen);
        Assert.Contains("da sistemare", vm.SummaryText);
    }

    [Fact]
    public async Task ViewModel_NonApreLaChecklistSeSvnCe()
    {
        var (vm, _, _) = CreateVm(new PrerequisiteProbes { FindSvn = () => "svn", FindClaude = () => "c" });

        await vm.InitializeAsync(CancellationToken.None);

        Assert.False(vm.IsOpen);
        Assert.Equal("Prerequisiti ✓", vm.SummaryText);
    }

    [Fact]
    public async Task ViewModel_PulsantePrincipaleDiGitHubLogin_LanciaLaConsole()
    {
        var (vm, launched, _) = CreateVm(new PrerequisiteProbes { FindGitHubCli = () => "gh" });
        await vm.InitializeAsync(CancellationToken.None);
        var row = vm.Items.Single(i => i.Id == PrerequisiteIds.GitHubLogin);

        await vm.RunActionAsync(row, primary: true, CancellationToken.None);

        Assert.Equal([ConsoleCommand.GitHubLogin], launched);
        Assert.Contains("Ricontrolla", vm.Feedback);
    }

    [Fact]
    public async Task ViewModel_SecondaryDiSvn_ApreLaPaginaDiDownload()
    {
        var (vm, _, urls) = CreateVm(new PrerequisiteProbes());
        await vm.InitializeAsync(CancellationToken.None);

        await vm.RunActionAsync(vm.Items.Single(i => i.Id == PrerequisiteIds.Svn), primary: false, CancellationToken.None);

        Assert.Equal([SvnEnvironment.DownloadUrl], urls);
    }

    [Fact]
    public async Task ViewModel_QuandoSvnDiventaDisponibile_NotificaIlPannello()
    {
        var found = false;
        var probes = new PrerequisiteProbes { FindSvn = () => found ? "svn" : null };
        var (vm, _, _) = CreateVm(probes);
        var notified = 0;
        vm.SvnBecameAvailable = _ => { notified++; return Task.CompletedTask; };

        await vm.InitializeAsync(CancellationToken.None);
        Assert.Equal(0, notified);

        found = true;
        await vm.RefreshAsync(CancellationToken.None);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task ViewModel_IntestazioneDiGruppoSoloSullaPrimaRiga()
    {
        var (vm, _, _) = CreateVm(new PrerequisiteProbes());
        await vm.InitializeAsync(CancellationToken.None);

        var headers = vm.Items.Where(i => i.HasGroupHeader).Select(i => i.GroupHeader).ToList();

        Assert.Equal(["Obbligatori", "Opzionali", "Provider AI (ne basta uno)"], headers);
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
