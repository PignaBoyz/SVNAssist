namespace SVNAssist.Services;

/// <summary>
/// Canale delle righe di notifica di svn (<c>U path</c>, <c>A path</c>...) verso la finestra di avanzamento. E' ambientale
/// (<see cref="AsyncLocal{T}"/>) per non cambiare le firme di <see cref="ISvnService"/>: vale solo per la chiamata avvolta da
/// <see cref="RunAsync{T}"/>, quindi i <c>svn status</c> del refresh non finiscono nell'elenco.
/// </summary>
internal static class SvnProgress
{
    private static readonly AsyncLocal<Action<string>?> _sink = new();

    internal static Action<string>? Sink => _sink.Value;

    internal static async Task<T> RunAsync<T>(Action<string> sink, Func<Task<T>> body)
    {
        _sink.Value = sink;
        try { return await body(); }
        finally { _sink.Value = null; }
    }
}
