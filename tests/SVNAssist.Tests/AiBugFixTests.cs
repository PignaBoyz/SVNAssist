using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Correzioni dai feedback 0.1.0: guida AI sempre visibile, errori mai silenziosi, file aggiunti, molti file, regole di commit.</summary>
public class AiBugFixTests : IDisposable
{
    private static CancellationToken Ct => CancellationToken.None;
    private readonly string _dir = Directory.CreateTempSubdirectory("svnassist-ai-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class EmptyAnswerAi : AiServiceBase
    {
        protected override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
            => Task.FromResult("  ");
    }

    private sealed class ThrowingAi(Exception ex) : IAiService
    {
        public Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct, string? projectInstructions = null)
            => Task.FromException<string>(ex);

        public Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct, string? projectInstructions = null, string? style = null)
            => Task.FromException<string>(ex);
    }

    private (CommitStore Vm, FakeAiFactory Ai, NoopAiPrompt Prompt) CreateVm()
    {
        var ai = new FakeAiFactory();
        var prompt = new NoopAiPrompt();
        var path = Path.Combine(_dir, "a.cs");
        var vm = new CommitStore(new StubSettings(), new FakeSvnService(), ai, prompt, () => [path], () => _dir);
        return (vm, ai, prompt);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── Bug 1: la guida compare a ogni click, non una volta sola ──

    [Fact]
    public async Task SenzaProvider_LaGuidaVieneMostrataAOgniClick()
    {
        var (vm, ai, prompt) = CreateVm();
        ai.Available = false;

        await vm.GenerateAsync(Ct);
        await vm.GenerateAsync(Ct);
        await vm.GenerateAsync(Ct);

        Assert.Equal(3, prompt.Calls);
        Assert.False(vm.IsBusy);
        Assert.Contains("Genera con AI", vm.AiStatus);
    }

    [Theory]
    [InlineData("auto", ConsoleCommand.GitHubLogin)]
    [InlineData("copilot", ConsoleCommand.GitHubLogin)]
    [InlineData("openai", ConsoleCommand.GitHubLogin)]
    [InlineData("claudecode", ConsoleCommand.ClaudeLogin)]
    [InlineData("claude", null)]
    internal void LoginCommand_PerProvider(string provider, ConsoleCommand? expected)
        => Assert.Equal(expected, AiProviderGuide.LoginCommand(provider));

    // ── Bug 2: errori sempre visibili ──

    [Fact]
    public async Task RispostaVuota_DiventaErroreVisibile_NonUnSuccessoSenzaTesto()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EmptyAnswerAi().GenerateCommitMessageAsync("Index: a\n+x\n", "auto", Ct));
        Assert.Contains("vuoto", ex.Message);
    }

    private sealed class FixedAi(string answer) : AiServiceBase
    {
        protected override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct) => Task.FromResult(answer);
    }

    [Theory]
    [InlineData("```\nfeat: x\n\ncorpo\n```", "feat: x\n\ncorpo")]
    [InlineData("```text\nfeat: x\n```\n", "feat: x")]
    [InlineData("  ```markdown\r\nfix: y\r\n```  ", "fix: y")]
    [InlineData("\"feat: x\"", "feat: x")]
    [InlineData("```\n\"feat: x\"\n```", "feat: x")]
    [InlineData("```\nfeat: x", "feat: x")]              // blocco non chiuso
    [InlineData("feat: x\n```", "feat: x")]              // chiusura senza apertura
    [InlineData("```", "")]
    [InlineData("feat: usa \"a\" e \"b\"", "feat: usa \"a\" e \"b\"")]   // virgolette interne: intatte
    [InlineData("feat: x\n\n```\ncodice\n```\n\nfine", "feat: x\n\n```\ncodice\n```\n\nfine")]   // blocco nel corpo: intatto
    public void CleanAnswer_TogliBloccoMarkdownEVirgoletteCheAvvolgonoTutto(string raw, string expected)
        => Assert.Equal(expected, AiServiceBase.CleanAnswer(raw));

    [Fact]
    public async Task RispostaInBloccoDiCodice_ArrivaPulita_ESoloBloccoVuotoEErrore()
    {
        Assert.Equal("feat: x", await new FixedAi("```\nfeat: x\n```").GenerateCommitMessageAsync("diff", "italiano", Ct));
        Assert.Equal("fix: y", await new FixedAi("```text\nfix: y\n```").ImproveCommitMessageAsync("bozza", "diff", "italiano", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FixedAi("```\n```").GenerateCommitMessageAsync("diff", "italiano", Ct));
    }

    [Fact]
    public void RecentMessages_VociGiaSalvateConBloccoMarkdown_NormalizzateSenzaVuoteNeDoppioni()
    {
        var file = Path.Combine(_dir, "recent.json");
        File.WriteAllText(file, """{"r":["```","```\nfeat: x\n```","feat: x","\"fix: y\""]}""");
        var r = new RecentMessages(file);

        Assert.Equal(["feat: x", "fix: y"], r.Get("r"));
        r.Add("r", "```\nnuovo\n```");
        Assert.Equal(["nuovo", "feat: x", "fix: y"], r.Get("r"));
    }

    [Fact]
    public async Task ErroreDelProvider_FinisceNellaStatusBar_ETestoInvariato()
    {
        var (vm, ai, _) = CreateVm();
        vm.Text = "bozza";
        ai.Custom = new ThrowingAi(new HttpRequestException("401 Unauthorized"));

        await vm.GenerateAsync(Ct);

        Assert.Contains("Errore AI", vm.AiStatus);
        Assert.Contains("401", vm.AiStatus);
        Assert.Equal("bozza", vm.Text);
    }

    [Fact]
    public async Task AnnullamentoInaspettato_LasciaComunqueUnFeedback()
    {
        var (vm, ai, _) = CreateVm();
        ai.Custom = new ThrowingAi(new OperationCanceledException());

        await vm.GenerateAsync(Ct);

        Assert.NotEmpty(vm.AiStatus);
        Assert.DoesNotContain("generato con AI", vm.AiStatus);
    }

    // ── Bug 3: file aggiunti / vuoti / non testuali nell'elenco inviato ──

    [Fact]
    public void ElencoFile_IncludeAggiuntiEliminatiEVuoti()
    {
        var list = SvnService.BuildStatusList([("src\\Nuovo.cs", "A"), ("vuoto.txt", "A"), ("Vecchio.cs", "D"), ("img.png", "M")]);

        Assert.Contains("A  src/Nuovo.cs", list);
        Assert.Contains("A  vuoto.txt", list);
        Assert.Contains("D  Vecchio.cs", list);
        Assert.Contains("M  img.png", list);
    }

    [Theory]
    [InlineData("added", "A")]
    [InlineData("deleted", "D")]
    [InlineData("modified", "M")]
    [InlineData("replaced", "R")]
    public void StatusLetter_MappaGliStatiSvn(string item, string letter)
        => Assert.Equal(letter, SvnService.StatusLetter(item));

    // ── Bug 4: molti file ──

    [Fact]
    public void ElencoFile_MoltiFile_ResteLimitatoEDichiaraQuantiMancano()
    {
        var files = Enumerable.Range(0, 3000).Select(i => ($"src/cartella/File{i:D5}.cs", "M"));

        var list = SvnService.BuildStatusList(files);

        Assert.True(list.Length < 13_000);
        Assert.Contains("altri", list);
    }


    // ── Bug 5: regole di commit ──

    [Fact]
    public void Regole_NessunFile_Null()
        => Assert.Null(CommitInstructions.Load(_dir, userHome: Path.Combine(_dir, "home")));

    [Fact]
    public void Regole_RisalendoDallaWorkingCopy_TrovaCopilotInstructions()
    {
        Write(".github/copilot-instructions.md", "Usa Conventional Commits.");
        var wc = Path.Combine(_dir, "src", "app");
        Directory.CreateDirectory(wc);

        var rules = CommitInstructions.Load(wc, userHome: Path.Combine(_dir, "home"));

        Assert.Equal("Usa Conventional Commits.", rules);
    }

    [Fact]
    public void Regole_LeggeAncheClaudeMdEAgentsMd_GlobaleEDiProgetto()
    {
        Write("home/.claude/CLAUDE.md", "Globale Claude.");
        Write("CLAUDE.md", "Progetto Claude.");
        Write("AGENTS.md", "Progetto Agents.");

        var rules = CommitInstructions.Load(_dir, userHome: Path.Combine(_dir, "home"))!;

        Assert.Equal("Globale Claude.\n\nProgetto Claude.\n\nProgetto Agents.", rules);
    }

    [Fact]
    public void Regole_FileUtenteGlobale_ESpecificheDelProgettoDopo()
    {
        Write("home/.copilot/copilot-instructions.md", "Regola globale.");
        Write(".github/copilot-instructions.md", "Regola progetto.");

        var rules = CommitInstructions.Load(_dir, userHome: Path.Combine(_dir, "home"))!;

        Assert.True(rules.IndexOf("globale", StringComparison.Ordinal) < rules.IndexOf("progetto", StringComparison.Ordinal));
    }

    [Fact]
    public void Regole_InstructionsConApplyTo_SoloQuelleGlobali()
    {
        Write(".github/instructions/tutto.instructions.md", "---\napplyTo: \"**\"\n---\nMessaggi in inglese.");
        Write(".github/instructions/ts.instructions.md", "---\napplyTo: \"**/*.ts\"\n---\nSolo TypeScript.");

        var rules = CommitInstructions.Load(_dir, userHome: Path.Combine(_dir, "home"))!;

        Assert.Contains("Messaggi in inglese.", rules);
        Assert.DoesNotContain("TypeScript", rules);
        Assert.DoesNotContain("applyTo", rules);
    }

    [Fact]
    public void Regole_FileEnorme_VieneLimitato()
    {
        Write(".github/copilot-instructions.md", new string('x', 50_000));

        var rules = CommitInstructions.Load(_dir, userHome: Path.Combine(_dir, "home"))!;

        Assert.True(rules.Length <= CommitInstructions.MaxFileChars + 50);
    }

    [Fact]
    public async Task Regole_ArrivanoAlPromptDiSistema()
    {
        string? system = null;
        var ai = new CapturingAi(s => system = s);

        await ai.GenerateCommitMessageAsync("Index: a\n+x\n", "auto", Ct, "Prefisso obbligatorio: [JIRA-123]");

        Assert.Contains("[JIRA-123]", system);
    }

    private sealed class CapturingAi(Action<string> capture) : AiServiceBase
    {
        protected override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            capture(systemPrompt);
            return Task.FromResult("ok");
        }
    }

    [Fact]
    public async Task ViewModel_PassaLeRegoleDellaWorkingCopyAlProvider()
    {
        Write(".github/copilot-instructions.md", "Regola X.");
        var (vm, ai, _) = CreateVm();

        await vm.GenerateAsync(Ct);

        Assert.Contains("Regola X.", ai.Service.Instructions.Single());
        Assert.Contains("Regole di commit", vm.AiStatus);
    }

    // ── Bug 6: valori fuori lista ──

    [Fact]
    public void ModelloOpenAiFuoriLista_SuGitHubModels_RicadeSulDefault()
        => Assert.Equal(SettingDefinitions.DefaultAiModel,
            AiServiceFactory.NormalizeOpenAiModel(SettingDefinitions.GitHubModelsEndpoint, "modello-sparito"));

    [Fact]
    public void ModelliConBarraEPunto_SonoNellaListaDeiValoriAmmessi()
    {
        Assert.Contains("openai/gpt-4.1", SettingDefinitions.KnownAiModels);
        Assert.Contains("claude-sonnet-4.5", SettingDefinitions.KnownCopilotModels);
        Assert.Equal("openai/gpt-4.1", AiServiceFactory.NormalizeOpenAiModel(SettingDefinitions.GitHubModelsEndpoint, "openai/gpt-4.1"));
    }
}
