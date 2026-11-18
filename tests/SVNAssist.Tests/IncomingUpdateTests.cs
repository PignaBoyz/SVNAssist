using System.Text.Json;
using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;
using Xunit;

namespace SVNAssist.Tests;

/// <summary>Anteprima di SVN Update (opzione aiExplainIncoming): spenta = comportamento di sempre; accesa = elenco + riassunto AI, poi Aggiorna/Annulla.</summary>
public class IncomingUpdateTests
{
    private const string Wc = @"C:\wc";

    private sealed class Fixture : IDisposable
    {
        public FakeSvnService Svn { get; } = new();
        public FakeAiFactory Ai { get; } = new();
        public StubSettings Settings { get; } = new();
        public ChangesStore Store { get; }

        public Fixture(bool explain, long wcRev = 7, long head = 9)
        {
            Settings.ExplainIncoming = explain;
            Svn.InfoRevision = wcRev;
            Svn.HeadRevision = head;
            Svn.RangeLog =
            [
                new SvnLogEntry(8, "anna", DateTimeOffset.Now, "Corretto il calcolo IVA", [new SvnLogPath("M", "/trunk/Core/Iva.cs")]),
                new SvnLogEntry(9, "bruno", DateTimeOffset.Now, "Nuova pagina ordini", [new SvnLogPath("A", "/trunk/Web/Ordini.cs"), new SvnLogPath("M", "/trunk/Web/web.config")]),
            ];
            Store = new ChangesStore(Settings, Svn, Ai, new NoopAiPrompt(), new FakeDialogs(), new FakeHost(),
                new PrereqStore(new PrerequisitesService(Settings, new PrerequisiteProbes())), recentTargetsFile: Path.GetTempFileName())
            { WorkingCopyPath = Wc, TempRoot = Path.Combine(Path.GetTempPath(), "SVNAssist_Inc_" + Guid.NewGuid().ToString("N")[..8]) };
        }

        public ChangesState Snap => (ChangesState)Store.GetSnapshot();
        public Task Cmd(string name) => Store.DispatchAsync(name, JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        public Task AiDone() => Store.Incoming.AiTask ?? Task.CompletedTask;
        public void Dispose() => Store.Dispose();
    }

    [Fact]
    public async Task FlagSpento_UpdateParteSubito_NessunaChiamataAiNeLog()
    {
        using var f = new Fixture(explain: false);
        await f.Cmd("update");

        Assert.Equal(1, f.Svn.Updates);
        Assert.Null(f.Snap.Incoming);
        Assert.Empty(f.Svn.RangeCalls);
        Assert.Empty(f.Ai.Service.IncomingPrompts);
    }

    [Fact]
    public async Task FlagAcceso_MostraRevisioniInArrivoEAspettaLaScelta()
    {
        using var f = new Fixture(explain: true);
        await f.Cmd("update");
        await f.AiDone();

        Assert.Equal(0, f.Svn.Updates);   // nessun update finche' l'utente non sceglie
        var inc = f.Snap.Incoming!;
        Assert.Equal(2, inc.Count);
        Assert.Equal([9L, 8L], inc.Items.Select(i => i.Rev));
        Assert.Equal("done", inc.Ai.State);
        Assert.Contains("Core", inc.Ai.Text);
        Assert.Equal((8L, 9L), (f.Svn.RangeCalls[0].Min, f.Svn.RangeCalls[0].Max));   // BASE+1 : HEAD

        await f.Cmd("incomingUpdate");
        Assert.Equal(1, f.Svn.Updates);
        Assert.Null(f.Snap.Incoming);
    }

    [Fact]
    public async Task Annulla_ChiudeSenzaAggiornare()
    {
        using var f = new Fixture(explain: true);
        await f.Cmd("update");
        await f.AiDone();
        await f.Cmd("incomingCancel");

        Assert.Null(f.Snap.Incoming);
        Assert.Equal(0, f.Svn.Updates);
    }

    [Fact]
    public async Task All_AiVannoSoloMessaggiENomiFile()
    {
        using var f = new Fixture(explain: true);
        await f.Cmd("update");
        await f.AiDone();

        var sent = Assert.Single(f.Ai.Service.IncomingPrompts);
        Assert.Contains("Corretto il calcolo IVA", sent);
        Assert.Contains("M /trunk/Core/Iva.cs", sent);
        Assert.Empty(f.Svn.FileDiffs);   // nessun diff letto
    }

    [Fact]
    public async Task NienteInArrivo_UpdateParteSenzaDialogo()
    {
        using var f = new Fixture(explain: true, wcRev: 9, head: 9);
        await f.Cmd("update");

        Assert.Equal(1, f.Svn.Updates);
        Assert.Null(f.Snap.Incoming);
    }

    [Fact]
    public async Task ServerNonRaggiungibile_UpdateParteComeSempre()
    {
        using var f = new Fixture(explain: true);
        f.Svn.HeadError = "offline";
        await f.Cmd("update");

        Assert.Equal(1, f.Svn.Updates);
        Assert.Null(f.Snap.Incoming);
    }

    [Fact]
    public async Task AiNonCollegata_ElencoSiVedeEUpdateFunziona()
    {
        using var f = new Fixture(explain: true);
        f.Ai.Available = false;
        await f.Cmd("update");
        await f.AiDone();

        Assert.Equal("none", f.Snap.Incoming!.Ai.State);
        Assert.Equal(2, f.Snap.Incoming.Items.Count);
        await f.Cmd("incomingUpdate");
        Assert.Equal(1, f.Svn.Updates);
    }

    [Fact]
    public async Task ErroreAiRiconoscibile_DaPulsanteConAzione_EUpdateFunziona()
    {
        using var f = new Fixture(explain: true);
        f.Ai.Service.IncomingError = "Claude Code: Failed to authenticate. API Error: 401";
        await f.Cmd("update");
        await f.AiDone();

        var ai = f.Snap.Incoming!.Ai;
        Assert.Equal("error", ai.State);
        Assert.Equal("incomingFixAuth", ai.Action!.Cmd);
        await f.Cmd("incomingUpdate");
        Assert.Equal(1, f.Svn.Updates);
    }

    [Fact]
    public async Task ErroreAiGenerico_SenzaPulsante()
    {
        using var f = new Fixture(explain: true);
        f.Ai.Service.IncomingError = "rete assente";
        await f.Cmd("update");
        await f.AiDone();

        Assert.Equal("error", f.Snap.Incoming!.Ai.State);
        Assert.Null(f.Snap.Incoming.Ai.Action);
    }

    [Fact]
    public void Prompt_RispettaIlLimiteDelProviderPiuStretto()
    {
        var big = Enumerable.Range(1, 500).Select(i => new SvnLogEntry(i, "u", DateTimeOffset.Now, new string('x', 400),
            [.. Enumerable.Range(0, 30).Select(k => new SvnLogPath("M", "/trunk/" + new string('p', 60) + k))])).ToList();
        Assert.True(IncomingStore.BuildPrompt(big).Length <= AiLimits.GitHubModels.MaxUserChars);
    }
}
