using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Risponde alle chiamate HTTP con risposte preparate (prima regola il cui pezzo di URL e metodo corrispondono) e le registra.</summary>
internal sealed class FakeAzureHandler : HttpMessageHandler
{
    internal readonly List<(HttpMethod Method, string Url, string Body)> Calls = [];
    private readonly List<(HttpMethod Method, string UrlPart, Func<HttpResponseMessage> Reply)> _rules = [];

    internal FakeAzureHandler On(HttpMethod method, string urlPart, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _rules.Add((method, urlPart, () => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        return this;
    }

    internal FakeAzureHandler OnHtml(HttpMethod method, string urlPart)
    {
        _rules.Add((method, urlPart, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") }));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Calls.Add((request.Method, url, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
        foreach (var r in _rules)
            if (r.Method == request.Method && url.Contains(r.UrlPart, StringComparison.Ordinal))
                return r.Reply();
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"nessuna regola\"}") };
    }
}

[Collection("Loc")]
public class AzureDevOpsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly AzureSettings S = new(true, "https://dev.azure.com/azienda", "IDC", "pat");
    private static readonly WorkItemInfo Bug = new(81018, "Bug", "Parametri AI non caricati", "Active", WorkItemText.Url("https://dev.azure.com/azienda", "IDC", 81018));

    // ── Formato del messaggio ────────────────────────────────────────

    [Fact]
    public void WithHeader_InCimaAlMessaggio_LeggibileSenzaEstensione()
    {
        var m = WorkItemText.WithHeader("Mostra il tasto\nseconda riga", Bug);

        Assert.Equal("Bug 81018 - Parametri AI non caricati\nhttps://dev.azure.com/azienda/IDC/_workitems/edit/81018\n\nMostra il tasto\nseconda riga", m);
        Assert.Equal([81018], WorkItemText.IdsIn(m));
        Assert.Equal(m, WorkItemText.WithHeader(m, Bug)); // gia' presente: invariato
    }

    [Fact]
    public void WithHeader_SecondoWorkItem_DopoIlPrimo_EWithoutHeaderLoToglie()
    {
        var task = Bug with { Id = 81020, Type = "Task", Title = "Pagina di attesa", Url = WorkItemText.Url("azienda", "IDC", 81020) };
        var m = WorkItemText.WithHeader(WorkItemText.WithHeader("corpo", Bug), task);

        Assert.Equal([81018, 81020], WorkItemText.IdsIn(m));
        Assert.EndsWith("\n\ncorpo", m);
        var only = WorkItemText.WithoutHeader(m, 81018);
        Assert.Equal([81020], WorkItemText.IdsIn(only));
        Assert.StartsWith("Task 81020 - Pagina di attesa", only);
    }

    [Fact]
    public void WithHeader_MessaggioVuoto()
        => Assert.Equal(WorkItemText.Header(Bug) + "\n", WorkItemText.WithHeader("", Bug));

    [Theory]
    [InlineData("fix #810", "810")]
    [InlineData("#81018", "81018")]
    [InlineData("fix#810", null)]
    [InlineData("fix #810 altro", null)]
    public void HashQuery_SoloInFondoDopoUnoSpazio(string text, string? query)
        => Assert.Equal(query, WorkItemText.HashQuery(text)?.Query);

    [Fact]
    public void Summary_SaltaLeIntestazioni()
        => Assert.Equal("Mostra il tasto", WorkItemText.Summary(WorkItemText.WithHeader("\nMostra il tasto\naltro", Bug)));

    [Theory]
    [InlineData("azienda", "https://dev.azure.com/azienda")]
    [InlineData("https://dev.azure.com/azienda/", "https://dev.azure.com/azienda")]
    [InlineData("azienda.visualstudio.com", "https://azienda.visualstudio.com")]
    public void NormalizeOrg(string input, string expected) => Assert.Equal(expected, AzureSettings.NormalizeOrg(input));

    // ── Strada degli stati ───────────────────────────────────────────

    private static readonly WorkItemState[] Agile =
    [
        new("New", "Proposed"), new("Active", "InProgress"), new("Resolved", "Resolved"), new("Closed", "Completed"), new("Removed", "Removed"),
    ];

    [Theory]
    [InlineData("New", "Closed", "Active,Resolved,Closed")]
    [InlineData("New", "Resolved", "Active,Resolved")]
    [InlineData("Active", "Resolved", "Resolved")]
    [InlineData("Closed", "Active", "Active")]      // indietro: diretto
    [InlineData("Active", "Removed", "Removed")]
    [InlineData("Active", "Active", "")]
    public void StatePath_PassaPerLeCategorieIntermedie(string from, string to, string steps)
        => Assert.Equal(steps, string.Join(",", StatePath.Steps(Agile, from, to)));

    // ── Client ───────────────────────────────────────────────────────

    [Fact]
    public async Task Get_LeggeTipoTitoloStato()
    {
        var h = new FakeAzureHandler().On(HttpMethod.Post, "workitemsbatch",
            """{"value":[{"id":81018,"fields":{"System.Id":81018,"System.WorkItemType":"Bug","System.Title":"Parametri AI","System.State":"Active"}}]}""");

        var items = await new AzureDevOpsClient(new HttpClient(h)).GetAsync(S, [81018], Ct);

        Assert.Equal(("Bug", "Parametri AI", "Active"), (items[0].Type, items[0].Title, items[0].State));
        Assert.Contains("/IDC/_workitems/edit/81018", items[0].Url);
    }

    [Fact]
    public async Task TokenNonValido_PaginaHtml_ErroreComprensibile()
    {
        var h = new FakeAzureHandler().OnHtml(HttpMethod.Post, "workitemsbatch");

        var ex = await Assert.ThrowsAsync<AzureDevOpsException>(() => new AzureDevOpsClient(new HttpClient(h)).GetAsync(S, [1], Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
    }

    [Fact]
    public async Task AddLink_SostituisceSoloIlinkDiSvnAssist()
    {
        var h = new FakeAzureHandler()
            .On(HttpMethod.Get, "workitems/81018?$expand=relations", """
                {"id":81018,"relations":[
                  {"rel":"Hyperlink","url":"https://x/a","attributes":{"comment":"SVN r100 su core19 · andrea"}},
                  {"rel":"Hyperlink","url":"https://wiki","attributes":{"comment":"documentazione"}},
                  {"rel":"Hyperlink","url":"https://x/b","attributes":{"comment":"SVN r120 su core19 · andrea"}}]}
                """)
            .On(HttpMethod.Patch, "workitems/81018", "{}");

        await new AzureDevOpsClient(new HttpClient(h)).AddLinkAsync(S, 81018, "https://svn/x?p=14121", "SVN r14121 su feature", Ct);

        var ops = JsonNode.Parse(h.Calls.Last().Body)!.AsArray();
        Assert.Equal(["remove:/relations/2", "remove:/relations/0", "add:/relations/-"], ops.Select(o => o!["op"] + ":" + o["path"]));
        Assert.Equal("SVN r14121 su feature", ops[2]!["value"]!["attributes"]!["comment"]!.GetValue<string>());
    }

    // ── Store ────────────────────────────────────────────────────────

    private static StubSettings Ready()
    {
        var s = new StubSettings();
        foreach (var (k, v) in new[] { (SettingDefinitions.AzEnabled, "true"), (SettingDefinitions.AzOrg, "https://dev.azure.com/azienda"), (SettingDefinitions.AzProject, "IDC"), (SettingDefinitions.AzPat, "pat") })
            s.Saved[k] = v;
        return s;
    }

    private const string Batch = """{"value":[{"id":81018,"fields":{"System.WorkItemType":"Bug","System.Title":"Parametri AI","System.State":"New"}}]}""";
    private const string States = """{"value":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"},{"name":"Resolved","category":"Resolved"},{"name":"Closed","category":"Completed"}]}""";

    [Fact]
    public async Task Link_MetteLIntestazione_EPreparaGliStatiConLaStrada()
    {
        var h = new FakeAzureHandler().On(HttpMethod.Post, "workitemsbatch", Batch).On(HttpMethod.Get, "/states", States)
            .On(HttpMethod.Patch, "validateOnly=true", "{}");
        var store = new AzureStore(Ready(), new HttpClient(h));
        await store.RefreshReadyAsync(Ct);

        var msg = await store.LinkAsync(81018, "fix #810", Ct);

        Assert.StartsWith("Bug 81018 - Parametri AI\n", msg);
        Assert.EndsWith("\n\nfix", msg);
        var closed = store.GetSnapshot().States.Single(x => x.Name == "Closed");
        Assert.Equal(["Active", "Resolved"], closed.Via);
        Assert.True(closed.Ok);
    }

    [Fact]
    public async Task Link_PassaggioRifiutato_StatoNonSceglibileConIlMotivo()
    {
        var h = new FakeAzureHandler().On(HttpMethod.Post, "workitemsbatch", Batch).On(HttpMethod.Get, "/states", States)
            .On(HttpMethod.Patch, "validateOnly=true", """{"message":"TF401320: serve il campo Resolved Reason"}""", HttpStatusCode.BadRequest);
        var store = new AzureStore(Ready(), new HttpClient(h));
        await store.RefreshReadyAsync(Ct);
        await store.LinkAsync(81018, "x", Ct);

        var resolved = store.GetSnapshot().States.Single(x => x.Name == "Resolved");
        Assert.False(resolved.Ok);
        Assert.Contains("Resolved Reason", resolved.Note);
        store.SetTargetState("Resolved");
        Assert.Equal(string.Empty, store.GetSnapshot().TargetState);
    }

    [Fact]
    public async Task DopoIlCommit_LinkEStatoPassoPasso_SiFermaAlPrimoRifiuto()
    {
        var h = new FakeAzureHandler()
            .On(HttpMethod.Post, "workitemsbatch", Batch)
            .On(HttpMethod.Get, "/states", States)
            .On(HttpMethod.Get, "$expand=relations", """{"id":81018,"relations":[]}""")
            .On(HttpMethod.Patch, "workitems/81018?api-version", "{}");
        var store = new AzureStore(Ready(), new HttpClient(h));

        var msg = WorkItemText.WithHeader("fix timeout", Bug);
        var summary = await store.AfterCommitAsync(new AzureStore.CommitInfo(14121, msg, "andrea", "core19_feature", "core19", "https://svn/b?p=14121", "Resolved"), Ct);

        var patches = h.Calls.Where(c => c.Method == HttpMethod.Patch).Select(c => JsonNode.Parse(c.Body)!.AsArray()).ToList();
        Assert.Equal(3, patches.Count); // link, Active, Resolved
        Assert.Equal("SVN r14121 su core19_feature (da core19) · andrea · fix timeout", patches[0].Last()!["value"]!["attributes"]!["comment"]!.GetValue<string>());
        Assert.Equal(["Active", "Resolved"], patches.Skip(1).Select(p => p[0]!["value"]!.GetValue<string>()));
        Assert.Contains("#81018", summary);
    }

    [Fact]
    public async Task Spenta_NessunaChiamata()
    {
        var h = new FakeAzureHandler();
        var store = new AzureStore(new StubSettings(), new HttpClient(h));

        Assert.Null(await store.AfterCommitAsync(new AzureStore.CommitInfo(1, WorkItemText.WithHeader("x", Bug), "a", "b", "", "u"), Ct));
        await store.OnMessageChangedAsync("fix #810", Ct);
        Assert.Empty(h.Calls);
        Assert.False(store.GetSnapshot().Ready);
    }

    [Fact]
    public void ParseCopySource_LaCartellaDelRamo()
    {
        const string xml = """
            <log><logentry revision="14000"><paths>
            <path action="A" kind="dir" copyfrom-path="/branches/core19" copyfrom-rev="13990">/branches/core19_feature_81018</path>
            <path action="M" kind="file">/branches/core19_feature_81018/x.txt</path>
            </paths></logentry></log>
            """;

        Assert.Equal("/branches/core19", SvnService.ParseCopySource(xml));
        Assert.Null(SvnService.ParseCopySource("<log/>"));
    }
}

public class AzureKeepHeadersTests
{
    private static readonly WorkItemInfo Bug = new(81018, "Bug", "Parametri AI", "Active", WorkItemText.Url("azienda", "IDC", 81018));

    [Fact]
    public void AiRiscriveIlMessaggio_LIntestazioneRestaInCima()
    {
        var before = WorkItemText.WithHeader("bozza", Bug);

        Assert.Equal(WorkItemText.Header(Bug) + "\n\nfix: timeout connessione", WorkItemText.KeepHeaders(before, "fix: timeout connessione"));
        // l'AI ha copiato o spostato l'intestazione: una sola, in cima
        Assert.Equal(WorkItemText.Header(Bug) + "\n\nfix: x", WorkItemText.KeepHeaders(before, "fix: x\n" + WorkItemText.Header(Bug)));
        Assert.Equal("solo ai", WorkItemText.KeepHeaders("bozza senza work item", "solo ai"));
    }
}

[Collection("Loc")]
public class AzureAuthTests
{
    [Fact]
    public void ParseCliToken_ScadenzaDaExpiresOn()
    {
        var (token, expires) = AzureTokens.ParseCliToken("""{"accessToken":"eyJ","expiresOn":"2026-11-26 22:10:00.000000","expires_on":1795727400,"tokenType":"Bearer"}""");

        Assert.Equal("eyJ", token);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795727400), expires);
        Assert.Throws<AzureDevOpsException>(() => AzureTokens.ParseCliToken("{}"));
    }

    [Fact]
    public void CliError_MessaggiComprensibili()
    {
        Assert.Contains("Installa Azure CLI", AzureTokens.CliError("'az' is not recognized as an internal or external command"));
        Assert.Contains("az login", AzureTokens.CliError("ERROR: Please run 'az login' to setup account."));
    }

    [Theory]
    [InlineData("pat", "", "", false)]
    [InlineData("pat", "x", "", true)]
    [InlineData("cli", "", "", true)]
    [InlineData("sso", "", "", false)]
    [InlineData("sso", "", "client-id", true)]
    public void Configured_SecondoIlModoDiAccesso(string auth, string pat, string clientId, bool configured)
        => Assert.Equal(configured, new AzureSettings(true, "https://dev.azure.com/a", "P", pat, Auth: auth, ClientId: clientId).Configured);

    [Fact]
    public void Console_AzureCli()
    {
        Assert.Equal(("cmd.exe", "/k az login --allow-no-subscriptions"), ConsoleLauncher.Build(ConsoleCommand.AzureLogin));
        Assert.Contains("Microsoft.AzureCLI", ConsoleLauncher.Build(ConsoleCommand.InstallAzureCli).Arguments);
    }
}
