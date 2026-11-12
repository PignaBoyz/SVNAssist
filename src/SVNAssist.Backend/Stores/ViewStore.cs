using System.Text.Json;

namespace SVNAssist.Stores;

/// <summary>
/// Una vista della UI (changes, log): produce lo snapshot completo e riceve i comandi (protocollo docs/ui-protocol.md).
/// Nessuna dipendenza da Visual Studio ne' da JSON-RPC: il trasporto sta in <see cref="BackendServer"/>.
/// </summary>
internal abstract class ViewStore
{
    /// <summary>Lo stato e' cambiato: chi ascolta rimanda lo snapshot (con coalescenza).</summary>
    public event Action? Changed;

    /// <summary>Lo snapshot corrente, conforme allo schema della vista.</summary>
    internal abstract object GetSnapshot();

    /// <summary>Esegue il comando <paramref name="name"/> con gli argomenti <paramref name="args"/> (oggetto JSON).
    /// Il risultato (di solito <c>null</c>) va nella reply; un'eccezione diventa <c>ok:false</c>.</summary>
    internal abstract Task<object?> DispatchAsync(string name, JsonElement args, CancellationToken ct);

    /// <summary>La pagina ha inviato <c>ready</c>: avvia il caricamento iniziale (senza bloccare la risposta).</summary>
    internal virtual Task OnReadyAsync(CancellationToken ct) => Task.CompletedTask;

    protected void NotifyChanged() => Changed?.Invoke();
}

/// <summary>Lettura tipizzata e validata degli argomenti dei comandi (arrivano dalla UI: non ci si fida).</summary>
internal static class Args
{
    private static JsonElement Prop(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            throw new ArgumentException(Loc.T("Argomento mancante: {0}", name));
        return value;
    }

    internal static string Str(JsonElement args, string name)
    {
        var value = Prop(args, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new ArgumentException(Loc.T("Argomento non valido: {0}", name));
    }

    internal static long Long(JsonElement args, string name)
    {
        var value = Prop(args, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : throw new ArgumentException(Loc.T("Argomento non valido: {0}", name));
    }

    internal static bool Bool(JsonElement args, string name)
    {
        var value = Prop(args, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException(Loc.T("Argomento non valido: {0}", name));
    }

    /// <summary>Stringa che deve essere uno dei valori ammessi.</summary>
    internal static string OneOf(JsonElement args, string name, params string[] allowed)
    {
        var value = Str(args, name);
        return allowed.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ArgumentException(Loc.T("Valore non valido per {0}: {1}", name, value));
    }
}
