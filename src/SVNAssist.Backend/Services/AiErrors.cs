using System.Text.RegularExpressions;

namespace SVNAssist.Services;

internal enum AiErrorKind
{
    /// <summary>Errore qualsiasi: si mostra il messaggio vero del provider.</summary>
    Other,

    /// <summary>Il provider dice che il modello non esiste piu' / non e' abilitato / non e' supportato.</summary>
    ModelUnavailable,

    /// <summary>Il provider non ha risposto: puo' dipendere dal modello scelto (es. Copilot con un modello ritirato).</summary>
    MaybeModel,

    /// <summary>Accesso scaduto, mancante o rifiutato (login, sessione OAuth, token o API key non validi): la UI propone di rifare l'accesso.</summary>
    Auth,
}

/// <summary>
/// Riconosce dal testo dell'errore se la causa e' il modello scelto (ritirato, rinominato, non abilitato): in quel caso
/// la UI propone "Aggiorna elenco modelli". Si lavora sul testo perche' i provider (SDK Anthropic, REST OpenAI, runtime Copilot)
/// non hanno un'eccezione comune, e <see cref="FallbackAiService"/> riunisce gli errori in una sola stringa.
/// </summary>
internal static class AiErrors
{
    private static readonly Regex ModelGone = new(
        @"model_not_found|model_not_supported|model_not_available|unknown[ _]model|invalid[ _]model|unsupported[ _]model|unavailable[ _]model" +
        @"|not_found_error[^\n]{0,300}model" +
        @"|\bmodel\b[^\n]{0,160}\b(not (be )?(found|available|supported|enabled|exist)|does not exist|doesn't exist|is unavailable|deprecated|retired|decommissioned|no longer|not accessible|unknown|invalid|unsupported)" +
        @"|\b(not (be )?(found|available|supported)|does not exist|no longer (available|supported))\b[^\n]{0,80}\bmodel\b" +
        @"|modello[^\n]{0,120}(non (è|e'|trovato|disponibile|supportato|esiste)|ritirato)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthFailed = new(
        @"failed to authenticate|authentication[_ ](error|failed|required)|not (logged|signed) in|log ?in required|please (log|sign) ?in|/login\b|session (has )?expired|token (has )?expired|expired token" +
        @"|invalid[ _-](x-)?api[ _-]key|incorrect api key|bad credentials|unauthori[sz]ed|\b401\b|invalid[ _]token|not authenticated|copilot non è collegato",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static AiErrorKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return AiErrorKind.Other;
        if (AuthFailed.IsMatch(message))
            return AiErrorKind.Auth;
        if (ModelGone.IsMatch(message))
            return AiErrorKind.ModelUnavailable;
        return message.Contains(Loc.T("Copilot non ha risposto: riprova o scegli un modello diverso."), StringComparison.Ordinal)
            ? AiErrorKind.MaybeModel
            : AiErrorKind.Other;
    }

    /// <summary>
    /// Provider (<c>copilot</c>, <c>claudecode</c>, <c>claude</c>, <c>openai</c>) che ha causato l'errore, se il messaggio lo nomina
    /// (la catena di fallback scrive "Nome: errore" per riga); preferisce la riga che parla del modello. <c>null</c> se non si capisce.
    /// </summary>
    internal static string? ProviderOf(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        string? firstNamed = null;
        foreach (var line in message.Split('\n'))
        {
            var id = line.TrimStart() switch
            {
                var l when l.StartsWith("GitHub Copilot:", StringComparison.Ordinal) => "copilot",
                var l when l.StartsWith("Claude Code:", StringComparison.Ordinal) => "claudecode",
                var l when l.StartsWith("Claude API:", StringComparison.Ordinal) => "claude",
                var l when l.StartsWith(Loc.T("OpenAI-compatibile") + ":", StringComparison.Ordinal) => "openai",
                _ => null,
            };
            if (id is null)
                continue;
            if (Classify(line) != AiErrorKind.Other)
                return id;
            firstNamed ??= id;
        }

        return firstNamed;
    }
}
