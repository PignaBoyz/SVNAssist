using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>
/// Gli snapshot del backend devono rispettare gli schemi del contratto UI (src/SVNAssist.UI.Web/contract),
/// gli stessi su cui girano i test JS del mock-bridge e della UI.
/// </summary>
public class SnapshotSchemaTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    internal static string ContractPath(string file) => Path.Combine(AppContext.BaseDirectory, "contract", file);

    internal static JsonSchema Schema(string name) => JsonSchema.FromText(File.ReadAllText(ContractPath(name + ".schema.json")));

    /// <summary>Errori di validazione (vuoto = conforme).</summary>
    internal static List<string> Validate(string schemaName, string json)
    {
        var result = Schema(schemaName).Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List });
        return result.IsValid
            ? []
            : result.Details.Where(d => d.Errors is { Count: > 0 })
                .Select(d => $"{d.InstanceLocation}: {string.Join("; ", d.Errors!.Values)}").ToList();
    }

    private static void AssertValid(string schemaName, string json)
        => Assert.True(Validate(schemaName, json) is { Count: 0 } errors, string.Join("\n", Validate(schemaName, json)) + "\n" + json);

    // ── Il validatore funziona (fixture ufficiali) ───────────────────

    [Theory]
    [InlineData("changes-state", "changes")]
    [InlineData("log-state", "log")]
    [InlineData("diff-state", "diff")]
    public void LeFixtureRispettanoGliSchemi(string schema, string fixture)
        => AssertValid(schema, File.ReadAllText(ContractPath($"fixtures/{fixture}.json")));

    [Fact]
    public void LoSchemaRifiutaStatiNonValidi()
    {
        var bad = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures/changes.json")))!;
        bad["view"] = "griglia";
        bad["sections"]![0]!["rows"]![0]!["extra"] = 1;
        bad.AsObject().Remove("status");

        Assert.NotEmpty(Validate("changes-state", bad.ToJsonString()));
    }

    // ── Snapshot reali ───────────────────────────────────────────────

    private static ChangesStore NewStore(FakeSvnService svn)
        => new(new StubSettings(), svn, new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
            new PrereqStore(new PrerequisitesService(new StubSettings(), new PrerequisiteProbes())))
        {
            WorkingCopyPath = @"C:\wc",
        };

    private static FakeSvnService SvnWithEverything()
    {
        var svn = new FakeSvnService();
        svn.Statuses.AddRange(
        [
            new("Core/Services/A.cs", SvnStatusKind.Modified),
            new("Core/Services/B.cs", SvnStatusKind.Added),
            new("Core/Old.cs", SvnStatusKind.Deleted),
            new("Web/Conf.cs", SvnStatusKind.Conflicted),
            new("Web/Tree.cs", SvnStatusKind.Conflicted, IsTreeConflict: true),
            new("Web/Gone.cs", SvnStatusKind.Missing),
            new("Web/R.cs", SvnStatusKind.Replaced),
            new("Web/Obstructed.cs", SvnStatusKind.Obstructed),
            new("docs/notes.txt", SvnStatusKind.Unversioned),
            new("docs/more.txt", SvnStatusKind.Unversioned),
            new("bin", SvnStatusKind.Unversioned),
            new(".", SvnStatusKind.Modified),
        ]);
        return svn;
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("list")]
    public async Task SnapshotChanges_ConTuttiGliStati_RispettaLoSchema(string view)
    {
        var store = NewStore(SvnWithEverything());
        await store.RefreshAsync(Ct);
        await store.DispatchAsync("setView", JsonSerializer.SerializeToElement(new { view }), Ct);
        await store.DispatchAsync("toggleFolder", JsonSerializer.SerializeToElement(new { key = "changes:Web" }), Ct);
        await store.DispatchAsync("prereqToggle", JsonSerializer.SerializeToElement(new { }), Ct);
        await store.Prerequisites.RefreshAsync(Ct);
        store.CommitMessage.SetTextQuietly("Messaggio con \"virgolette\", accenti è ù e\nnewline");

        AssertValid("changes-state", UiJson.Serialize(store.GetSnapshot()));
    }

    [Fact]
    public void SnapshotChanges_StatoIniziale_RispettaLoSchema()
        => AssertValid("changes-state", UiJson.Serialize(new ChangesStore(
            new StubSettings(), new FakeSvnService(), new FakeAiFactory(), new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
            new PrereqStore(new PrerequisitesService(new StubSettings(), new PrerequisiteProbes()))).GetSnapshot()));

    [Fact]
    public async Task SnapshotChanges_HaLeStesseChiaviDellaFixtureUfficiale()
    {
        var store = NewStore(SvnWithEverything());
        await store.RefreshAsync(Ct);
        var ours = JsonNode.Parse(UiJson.Serialize(store.GetSnapshot()))!.AsObject();
        var fixture = JsonNode.Parse(File.ReadAllText(ContractPath("fixtures/changes.json")))!.AsObject();

        static IEnumerable<string> Keys(JsonNode? n) => n!.AsObject().Select(p => p.Key).OrderBy(k => k);
        Assert.Equal(Keys(fixture), Keys(ours));
        Assert.Equal(Keys(fixture["commit"]), Keys(ours["commit"]));
        Assert.Equal(Keys(fixture["theme"]), Keys(ours["theme"]));
        Assert.Equal(Keys(fixture["prereq"]), Keys(ours["prereq"]));
        Assert.Equal(Keys(fixture["sections"]![0]), Keys(ours["sections"]![0]));
        Assert.Equal(Keys(fixture["sections"]![0]!["rows"]![0]!["can"]), Keys(ours["sections"]![0]!["rows"]![0]!["can"]));
    }

    [Fact]
    public async Task SnapshotLog_RispettaLoSchema()
    {
        var svn = new FakeSvnService { LogEntryCount = 3 };
        var store = new LogStore(svn, new StubSettings());
        AssertValid("log-state", UiJson.Serialize(store.GetSnapshot()));

        await store.ApplyAutoDetectedWorkingCopyAsync(@"C:\wc", Ct);
        await store.DispatchAsync("logSelect", JsonSerializer.SerializeToElement(new { rev = 2 }), Ct);

        AssertValid("log-state", UiJson.Serialize(store.GetSnapshot()));
    }

    [Fact]
    public void SnapshotLog_ConPercorsi_RispettaLoSchema()
    {
        var entry = new SvnLogEntry(5, "mario", new DateTimeOffset(2026, 11, 10, 17, 42, 0, TimeSpan.Zero), "msg",
            [new SvnLogPath("M", "/trunk/A.cs"), new SvnLogPath("A", "/trunk/B.cs")]);
        var state = new LogState(@"C:\wc", "1 revisioni caricate.", false, false, false, 5,
            [new LogEntryDto(entry.Revision, entry.Author, entry.Date.ToString("o"), entry.Message,
                entry.Paths!.Select(p => new LogPathDto(p.Action, p.Path)).ToList())], "Index: x", new LogFilterDto("", "", "", "", ""), null);

        AssertValid("log-state", UiJson.Serialize(state));
    }

    [Fact]
    public void SnapshotDiff_RispettaLoSchema()
        => AssertValid("diff-state", UiJson.Serialize(new DiffStore().GetSnapshot()));
}
