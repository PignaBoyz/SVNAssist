using System.Text.Json;
using System.Text.RegularExpressions;

namespace SVNAssist.Tests;

/// <summary>
/// Opzioni moderne di VS 2026: registration.json (quello che Tools > Options mostra) e UnifiedOptions.Map (quello che l'host
/// legge e scrive) devono avere le stesse chiavi, gli stessi tipi e gli stessi default. L'host e' net472 e non e' referenziato
/// dai test: si confrontano i due file.
/// </summary>
public class UnifiedOptionsRegistrationTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void RegistrationEMap_Coincidono()
    {
        var json = File.ReadAllText(Path.Combine(Root(), "src", "SVNAssist.Host", "Options", "registration.json"));
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var props = doc.RootElement.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        var code = File.ReadAllText(Path.Combine(Root(), "src", "SVNAssist.Host", "Options", "UnifiedOptions.cs"));
        var map = Regex.Matches(code, @"\(""(\w+)"", ""([\w.]+)"", Kind\.(\w+), ""([^""]*)""\)").Select(m => (Key: m.Groups[1].Value, Moniker: m.Groups[2].Value, Kind: m.Groups[3].Value, Default: m.Groups[4].Value)).ToList();

        Assert.NotEmpty(map);
        Assert.Equal(props.Keys.OrderBy(k => k), map.Select(m => m.Moniker).OrderBy(k => k));
        foreach (var m in map)
        {
            var p = props[m.Moniker];
            Assert.EndsWith("." + m.Key, m.Moniker);
            var type = p.GetProperty("type").GetString();
            Assert.Equal(m.Kind switch { "Bool" => "boolean", "Int" => "integer", _ => "string" }, type);
            var def = p.GetProperty("default");
            var text = def.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => def.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), _ => def.GetString() };
            Assert.Equal(m.Default, text);
            if (p.TryGetProperty("enum", out var e))
            {
                Assert.Contains(m.Default, e.EnumerateArray().Select(x => x.GetString()));
                Assert.Equal(e.GetArrayLength(), p.GetProperty("enumItemLabels").GetArrayLength());
            }
        }

        // nessun segreto nelle opzioni moderne: chiavi API e token restano cifrati e si inseriscono dal pannello
        Assert.DoesNotContain(map, m => m.Key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || m.Key == "azPat");
    }
}
