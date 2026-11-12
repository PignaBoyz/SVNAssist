using System.Net;
using System.Net.Http;
using RichardSzalay.MockHttp;
using SVNAssist.Services;

namespace SVNAssist.Tests;

/// <summary>Collaudo aziendale: risposte non JSON, Copilot vuoto, prompt con molti file e merge.</summary>
public class AiRobustnessTests
{
    private const string Endpoint = "https://api.test.com/v1/chat/completions";

    [Theory]
    [InlineData("application/json", "0")]
    [InlineData("text/html", "<html><body>Proxy aziendale sk-segreto</body></html>")]
    [InlineData("application/json", "{\"altro\":1}")]
    public async Task OpenAi_RispostaNonJsonChatCompletions_ErroreChiaroConCodiceEInizioRisposta(string type, string body)
    {
        var mock = new MockHttpMessageHandler();
        mock.When(Endpoint).Respond(type, body);
        var sut = new AiService(mock.ToHttpClient(), Endpoint, "m", "sk-segreto");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GenerateCommitMessageAsync("+x", "italiano", CancellationToken.None));

        Assert.Contains("HTTP 200", ex.Message);
        Assert.Contains(body[..1], ex.Message);
        Assert.DoesNotContain("sk-segreto", ex.Message);
        Assert.DoesNotContain("JsonReaderException", ex.Message);
    }

    private static CopilotAiService Copilot(Func<string, string, CancellationToken, Task<string>> core)
        => new(new HttpClient(), "tok", "") { CoreOverride = core };

    [Fact]
    public async Task Copilot_RispostaVuota_RiprovaConMessaggioPiuPiccolo()
    {
        var sizes = new List<int>();
        var sut = Copilot((_, m, _) =>
        {
            sizes.Add(m.Length);
            return Task.FromResult(sizes.Count == 1 ? "" : "fix: ok");
        });

        var result = await sut.GenerateCommitMessageAsync(
            "Index: a.cs\n" + string.Join("\n", Enumerable.Range(0, 500).Select(i => $"+riga {i}")), "italiano", CancellationToken.None);

        Assert.Equal("fix: ok", result);
        Assert.Equal(2, sizes.Count);
        Assert.True(sizes[1] < sizes[0]);
    }

    [Fact]
    public async Task Copilot_EccezioneSessione_RiprovaUnaVolta()
    {
        var calls = 0;
        var sut = Copilot((_, _, _) => ++calls == 1
            ? throw new InvalidOperationException("Session error: No response was returned.")
            : Task.FromResult("ok"));

        Assert.Equal("ok", await sut.GenerateCommitMessageAsync("+x", "italiano", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Copilot_SempreVuoto_MessaggioComprensibile()
    {
        var calls = 0;
        var sut = Copilot((_, _, _) => { calls++; return Task.FromResult(""); });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateCommitMessageAsync("+x", "italiano", CancellationToken.None));

        Assert.Contains("Copilot non ha risposto", ex.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OpenAi_RispostaVuotaPoiValida_RiprovaConMessaggioMinore()
    {
        var calls = 0;
        var mock = new MockHttpMessageHandler();
        mock.When(Endpoint).Respond(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("0", System.Text.Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"fix: ok\"}}]}", System.Text.Encoding.UTF8, "application/json") });
        var sut = new AiService(mock.ToHttpClient(), Endpoint, "m", "k");

        Assert.Equal("fix: ok", await sut.GenerateCommitMessageAsync("+x", "italiano", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    private sealed class RecordingAi(int budget) : AiServiceBase
    {
        private readonly object _lock = new();
        public List<string> Messages { get; } = [];
        public int Concurrent, MaxConcurrent;
        public int FailFirstMap;
        public List<string> Progresses { get; } = [];
        protected override int MaxUserMessageChars => budget;

        protected override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            var c = Interlocked.Increment(ref Concurrent);
            lock (_lock) { Messages.Add(userMessage); MaxConcurrent = Math.Max(MaxConcurrent, c); }
            await Task.Delay(2, ct);
            Interlocked.Decrement(ref Concurrent);
            if (userMessage.StartsWith("Blocco ") && Interlocked.Exchange(ref FailFirstMap, 0) == 1)
                return "";
            return userMessage.StartsWith("Blocco ") ? "- riassunto del blocco" : "ok finale";
        }

        public async Task<string> RunAsync(string diff, CancellationToken ct = default)
        {
            Progress = m => { lock (_lock) Progresses.Add(m); };
            return await GenerateCommitMessageAsync(diff, "italiano", ct);
        }
    }

    private static string TextFile(int n, int lines = 150)
        => $"Index: src/F{n}.cs\n===\n--- src/F{n}.cs\t(revision 5)\n+++ src/F{n}.cs\t(working copy)\n@@ -1 +1,40 @@\n"
           + string.Join("\n", Enumerable.Range(0, lines).Select(i => $"{(i % 3 == 0 ? '-' : '+')}F{n}_riga_{i}_contenuto_modificato_di_esempio")) + "\n";

    private static string MergeInfoFile(int n)
        => $"Index: lib/M{n}.cs\n===\n--- lib/M{n}.cs\t(revision 5)\n+++ lib/M{n}.cs\t(working copy)\n\nProperty changes on: lib/M{n}.cs\n___\nModified: svn:mergeinfo\n## -1 +1 ##\n   Merged /branches/x:r1-{n}\n";

    private static void AssertEveryLineCovered(string diff, RecordingAi ai)
    {
        var seen = ai.Messages.Where(m => m.StartsWith("Blocco ")).SelectMany(m => m.Split('\n')).ToHashSet();
        foreach (var line in diff.Split('\n').Where(l => l.StartsWith('+') || l.StartsWith('-')).Where(l => !l.StartsWith("+++") && !l.StartsWith("---")))
            Assert.Contains(line, seen);
    }

    [Theory]
    [InlineData(58, 150, 60_000)]
    [InlineData(1000, 40, 60_000)]
    [InlineData(1000, 40, 31_200)]
    public async Task MapReduce_TuttiIFile_OgniRigaInUnPromptMap_NessunPromptOltreBudget(int files, int lines, int budget)
    {
        var diff = "Elenco file selezionati (stato svn):\n" + string.Concat(Enumerable.Range(0, files).Select(i => $"M  src/F{i}.cs\n"))
                   + string.Concat(Enumerable.Range(0, files).Select(i => TextFile(i, lines)));
        var sut = new RecordingAi(budget);

        var result = await sut.RunAsync(diff);

        Assert.Equal("ok finale", result);
        AssertEveryLineCovered(diff, sut);
        Assert.All(sut.Messages, m => Assert.True(m.Length <= budget, $"{m.Length} > {budget}"));
        Assert.True(sut.MaxConcurrent <= AiServiceBase.MapConcurrency);
        var maps = sut.Messages.Count(m => m.StartsWith("Blocco "));
        Assert.True(maps > 1);
        // Il reduce (ultima richiesta) riceve i riassunti di tutti i blocchi: ogni etichetta [i/n] presente.
        var final = sut.Messages[^1];
        Assert.Contains($"[{maps}/{maps}]", final);
        Assert.Contains("[1/" + maps + "]", final);
        Assert.Contains(sut.Progresses, p => p.Contains("blocco") || p.Contains("block"));
        Console.WriteLine($"CALLS files={files} budget={budget}: map={maps} totale={sut.Messages.Count}");
    }

    [Fact]
    public async Task MapReduce_Merge_SoleProprietaInUnaRiga_RevisioniUnite_TestoVeroPresente()
    {
        var diff = "Elenco file selezionati (stato svn):\n"
                   + string.Concat(Enumerable.Range(0, 60).Select(i => $"M  lib/M{i}.cs\n"))
                   + string.Concat(Enumerable.Range(0, 60).Select(MergeInfoFile))
                   + string.Concat(Enumerable.Range(0, 20).Select(i => TextFile(i, 400)));
        var sut = new RecordingAi(31_200);

        await sut.RunAsync(diff);

        AssertEveryLineCovered(diff, sut);
        var final = sut.Messages[^1];
        Assert.Contains("60 file con sole modifiche di proprietà", final);
        Assert.Contains("Merged /branches/x:r1-0", final);
        Assert.DoesNotContain(sut.Messages.Where(m => m.StartsWith("Blocco ")), m => m.Contains("Property changes on"));
    }

    [Fact]
    public async Task DiffPiccolo_UnaSolaChiamata()
    {
        var sut = new RecordingAi(31_200);

        await sut.RunAsync(TextFile(1, 20));

        Assert.Single(sut.Messages);
    }

    [Fact]
    public async Task MapReduce_BloccoConRispostaVuota_RiprovaEConclude()
    {
        var sut = new RecordingAi(31_200) { FailFirstMap = 1 };

        var result = await sut.RunAsync(string.Concat(Enumerable.Range(0, 20).Select(i => TextFile(i, 300))));

        Assert.Equal("ok finale", result);
    }

    [Fact]
    public async Task MapReduce_Annullamento_Interrompe()
    {
        using var cts = new CancellationTokenSource();
        var sut = new RecordingAi(31_200);
        cts.CancelAfter(5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.RunAsync(string.Concat(Enumerable.Range(0, 400).Select(i => TextFile(i, 300))), cts.Token));
    }

    [Fact]
    public void ChunkDiff_FileEnorme_SpezzatoPerHunk_NessunaRigaPersa()
    {
        var big = "Index: g.cs\n===\n" + string.Concat(Enumerable.Range(0, 200).Select(h => $"@@ -{h} +{h} @@\n" + string.Concat(Enumerable.Range(0, 20).Select(l => $"+h{h}_l{l}\n"))));

        var chunks = AiServiceBase.ChunkDiff(big, 4000);

        Assert.All(chunks, c => Assert.True(c.Length <= 4000));
        var joined = string.Join("", chunks);
        for (var h = 0; h < 200; h++)
            Assert.Contains($"+h{h}_l19", joined);
    }
}

