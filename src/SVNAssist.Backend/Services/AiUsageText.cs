using System.Globalization;
using System.Text.RegularExpressions;

namespace SVNAssist.Services;

/// <summary>
/// Una sola specifica di visualizzazione del consumo per tutti i provider, nella lingua dell'interfaccia con i numeri nel formato della lingua:
/// <c>Sonnet 5.5 · 1.240 token in / 310 out · ≈ 0,04 $</c>. Il costo compare solo se lo riporta il provider (oggi la CLI di Claude Code).
/// </summary>
internal static class AiUsageText
{
    private static readonly Regex ClaudeId = new(@"^claude-([a-z]+)-(\d+(?:-\d{1,2}(?!\d))?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Nome breve: <c>claude-sonnet-5-5</c> diventa <c>Sonnet 5.5</c> (ricavato dall'id, nessuna tabella di modelli); gli altri id restano come sono.</summary>
    internal static string ShortModel(string model)
    {
        var id = AiModelIds.StripModelsPrefix(model.Trim())!;
        var m = ClaudeId.Match(id);
        return m.Success ? char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..].ToLowerInvariant() + " " + m.Groups[2].Value.Replace('-', '.') : id;
    }

    internal static CultureInfo NumberCulture => CultureInfo.GetCultureInfo(Loc.Lang == "en" ? "en-US" : "it-IT");

    /// <summary>Testo del consumo; vuoto se non c'e' nulla da dire.</summary>
    internal static string Format(AiUsage usage)
    {
        var c = NumberCulture;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(usage.Model))
            parts.Add(ShortModel(usage.Model!));
        if (usage.InputTokens > 0 || usage.OutputTokens > 0)
            parts.Add(Loc.T("{0} token in / {1} out", usage.InputTokens.ToString("N0", c), usage.OutputTokens.ToString("N0", c)));
        if (usage.CostUsd is { } cost)
            parts.Add(Loc.T("≈ {0} $", cost.ToString(cost >= 0.01m ? "N2" : "N4", c)));
        if (usage.AiCredits is { } credits)
        {
            var n = credits.ToString(credits >= 0.01m ? "N2" : "N4", c);
            parts.Add(usage.CreditsPartial ? Loc.T("≥ {0} crediti AI (parziale)", n) : Loc.T("{0} crediti AI", n));
        }
        return string.Join(" · ", parts);
    }
}
