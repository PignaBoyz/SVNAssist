using SVNAssist.Models;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>0.6.1: il riassunto di SVN Update parte dal diff vero di ogni revisione; i file sensibili solo per nome; crediti AI di Copilot nel testo di consumo.</summary>
[Collection("Loc")]
public sealed class IncomingDiffTests : IDisposable
{
    public IncomingDiffTests() => Loc.Lang = "it";

    public void Dispose() => Loc.Lang = "it";

    private const string Diff = "Index: src/Queue.cs\n===\n--- src/Queue.cs\n+++ src/Queue.cs\n@@ -10 +10 @@ void Pump()\n-lock(all) Work();\n+Work();\nIndex: .env\n===\n--- .env\n+++ .env\n@@ -1 +1 @@\n+PASSWORD=abc123\n";

    [Fact]
    public void Prompt_ContieIlDiffDellaRevisioneEIlNomeDelFileSensibile()
    {
        var log = new List<SvnLogEntry> { new(5, "andrea", DateTimeOffset.Now, "coda", [new SvnLogPath("M", "/trunk/src/Queue.cs")]) };

        var prompt = IncomingStore.BuildPrompt(log, out var included, new Dictionary<long, string> { [5] = Diff });

        Assert.Equal(1, included);
        Assert.Contains("r5 andrea: coda", prompt);
        Assert.Contains("-lock(all) Work();", prompt);
        Assert.Contains(".env (contenuto non inviato)", prompt);
        Assert.DoesNotContain("abc123", prompt);
    }

    [Fact]
    public void Prompt_SenzaDiffRestaAlElencoFile()
    {
        var log = new List<SvnLogEntry> { new(5, "andrea", DateTimeOffset.Now, "coda", [new SvnLogPath("M", "/trunk/src/Queue.cs")]) };

        Assert.Contains("M /trunk/src/Queue.cs", IncomingStore.BuildPrompt(log, out _, new Dictionary<long, string>()));
    }

    [Fact]
    public void Consumo_MostraICreditiAi_ESeParzialiLoDice()
    {
        Assert.Equal("gpt-6-luna · 1.200 token in / 300 out · 0,25 crediti AI", AiUsageText.Format(new AiUsage("gpt-6-luna", 1200, 300, null, 0.25m)));
        Assert.Contains("≥ 0,25 crediti AI (parziale)", AiUsageText.Format(new AiUsage("m", 1, 1, null, 0.25m, true)));
        Assert.DoesNotContain("crediti", AiUsageText.Format(new AiUsage("m", 1, 1)));
    }
}
