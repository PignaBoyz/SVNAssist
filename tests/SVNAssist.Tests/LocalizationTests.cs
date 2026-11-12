using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SVNAssist.Services;
using SVNAssist.Stores;

namespace SVNAssist.Tests;

/// <summary>Loc.Lang e' uno stato di processo: i test che lo cambiano non girano in parallelo con gli altri.</summary>
[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection;

/// <summary>Lingua dell'interfaccia (it/en): chiavi complete, risoluzione di "auto", cambio a caldo di stati e testi del backend.</summary>
[Collection("Loc")]
public sealed partial class LocalizationTests : IDisposable
{
    public LocalizationTests() => Loc.Lang = "it";

    public void Dispose() => Loc.Lang = "it";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SVNAssist.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Radice del repository non trovata.");
    }

    private const string Lit = "\"(?:[^\"\\\\]|\\\\.)*\"";

    [GeneratedRegex("(?:Loc\\.[TK]|LDisplayName|LDescription|LCategory)\\(\\s*(" + Lit + "(?:\\s*\\+\\s*" + Lit + ")*)")]
    private static partial Regex KeyCall();

    [GeneratedRegex(Lit)]
    private static partial Regex Literal();

    [GeneratedRegex("\\{\\d+(?::[^}]*)?\\}")]
    private static partial Regex Placeholder();

    private static string Unescape(string literal) => Regex.Replace(literal[1..^1], "\\\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });

    /// <summary>Tutte le chiavi (testi italiani) usate nel codice di Host e Backend.</summary>
    private static HashSet<string> KeysInSource()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
            foreach (Match call in KeyCall().Matches(File.ReadAllText(file)))
                keys.Add(string.Concat(Literal().Matches(call.Groups[1].Value).Select(l => Unescape(l.Value))));
        return keys;
    }

    [Fact]
    public void OgniChiaveDelCodice_HaLaTraduzioneInglese_ConGliStessiSegnaposto()
    {
        var keys = KeysInSource();
        Assert.True(keys.Count > 200, "Estrazione delle chiavi dal codice fallita: " + keys.Count);

        var missing = keys.Where(k => !Loc.En.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Chiavi senza traduzione inglese:\n" + string.Join("\n", missing));

        var wrongPlaceholders = keys.Where(k => !Placeholder().Matches(k).Select(m => m.Value).Order().SequenceEqual(Placeholder().Matches(Loc.En[k]).Select(m => m.Value).Order()))
            .ToList();
        Assert.True(wrongPlaceholders.Count == 0, "Segnaposto diversi tra italiano e inglese:\n" + string.Join("\n", wrongPlaceholders));
    }

    [Fact]
    public void OgniVoceInglese_CorrispondeAUnaChiaveDelCodice()
    {
        var keys = KeysInSource();
        var stale = Loc.En.Keys.Where(k => !keys.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Voci inglesi non usate dal codice:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void T_InItaliano_RitornaLaChiave_InInglese_LaTraduzione()
    {
        Assert.Equal("Annulla", Loc.T("Annulla"));
        Assert.Equal("Revert in progress...", Without("Revert in corso...", "en"));
        Assert.Equal("Commit completed: revision 7 (3 items).", Without("Commit completato: revisione {0} ({1} elementi).", "en", 7, 3));
        Assert.Equal("Commit completato: revisione 7 (3 elementi).", Loc.T("Commit completato: revisione {0} ({1} elementi).", 7, 3));
        Assert.Equal("testo non tradotto", Without("testo non tradotto", "en"));

        static string Without(string key, string lang, params object[] args)
        {
            Loc.Lang = lang;
            try { return Loc.T(key, args); }
            finally { Loc.Lang = "it"; }
        }
    }

    [Theory]
    [InlineData("auto", "it-IT", "it")]
    [InlineData("auto", "it", "it")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "fr-FR", "en")]
    [InlineData("auto", "", "en")]
    [InlineData("it", "en-US", "it")]
    [InlineData("en", "it-IT", "en")]
    public void Resolve_AutoSegueLaCulturaDiVs(string setting, string culture, string expected)
        => Assert.Equal(expected, Loc.Resolve(setting, culture));

    [Fact]
    public async Task HostSettings_UsaLaLingua_GiaRisoltaDallHost_EdItalianoSenzaValore()
    {
        var host = new FakeHost();
        var settings = new HostSettings(host);
        Assert.Equal("it", await settings.GetUiLanguageAsync(CancellationToken.None)); // nessun valore (test): italiano

        host.Settings["uiLanguage"] = "auto";
        host.Settings["uiLanguageEffective"] = "en";
        settings.Invalidate();
        Assert.Equal("en", await settings.GetUiLanguageAsync(CancellationToken.None));

        host.Settings["uiLanguage"] = "it";
        host.Settings["uiLanguageEffective"] = "it";
        settings.Invalidate();
        Assert.Equal("it", await settings.GetUiLanguageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CambioLingua_AGaldo_AggiornaStatoEdEnvelopeDiTutteLeFinestre()
    {
        var host = new FakeHost();
        var settings = new StubSettings { UiLanguage = "it" };
        var services = new BackendServices
        {
            Settings = settings,
            Svn = new FakeSvnService(),
            Ai = new FakeAiFactory(),
            AiSetupPrompt = new NoopAiPrompt(),
            Dialogs = new FakeDialogs(),
            Probes = new PrerequisiteProbes(),
            PrerequisiteActions = new PrerequisiteActions(),
        };
        using var server = new BackendServer(host, services) { CoalesceDelay = TimeSpan.FromMilliseconds(1) };

        foreach (var view in new[] { "changes", "log" })
            await server.DispatchAsync(view, $"{{\"v\":1,\"type\":\"ready\",\"view\":\"{view}\",\"id\":null}}");

        JsonNode Last(string view)
        {
            lock (host.Pushed)
                return host.Pushed.Where(p => p.View == view).Select(p => JsonNode.Parse(p.Json)!).Last(m => m["type"]!.GetValue<string>() == "state");
        }

        string SectionTitle(string id) => Last("changes")["state"]!["sections"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == id)!["title"]!.GetValue<string>();

        Assert.Equal("it", Last("changes")["lang"]!.GetValue<string>());
        Assert.Equal("Modifiche", SectionTitle("changes"));
        Assert.Equal("Inserisci il percorso della working copy e premi Carica log.", Last("log")["state"]!["msg"]!.GetValue<string>());

        settings.UiLanguage = "en";
        await server.SettingsChangedAsync();

        Assert.Equal("en", Last("changes")["lang"]!.GetValue<string>());
        Assert.Equal("Changes", SectionTitle("changes"));
        Assert.Equal("Unversioned", SectionTitle("unversioned"));
        Assert.Equal("en", Last("log")["lang"]!.GetValue<string>());
        Assert.Equal("Enter the path of the working copy and press Load log.", Last("log")["state"]!["msg"]!.GetValue<string>());
        lock (host.Pushed)
            foreach (var (_, json) in host.Pushed)
                Assert.Empty(SnapshotSchemaTests.Validate("envelope", json));

        settings.UiLanguage = "it";
        await server.SettingsChangedAsync();
        Assert.Equal("it", Last("changes")["lang"]!.GetValue<string>());
        Assert.Equal("Modifiche", SectionTitle("changes"));
    }

    [Fact]
    public void LivelloMessaggioAi_NonDipendeDallaLingua()
    {
        foreach (var lang in new[] { "it", "en" })
        {
            Loc.Lang = lang;
            Assert.Equal("err", CommitStore.LevelOf(Loc.T("Errore AI: {0}", "x"), busy: false));
            Assert.Equal("warn", CommitStore.LevelOf(Loc.T("Nessun diff disponibile."), busy: false));
            Assert.Equal("warn", CommitStore.LevelOf(Loc.T("Metti almeno un file in Staged per generare il messaggio."), busy: false));
            Assert.Equal("warn", CommitStore.LevelOf(Loc.T("Scrivi prima una bozza del messaggio da migliorare."), busy: false));
            Assert.Equal("warn", CommitStore.LevelOf(Loc.T("Operazione annullata: nessun dato è stato inviato."), busy: false));
            Assert.Equal("ok", CommitStore.LevelOf(Loc.T("Messaggio generato con AI") + ".", busy: false));
        }
    }
}
