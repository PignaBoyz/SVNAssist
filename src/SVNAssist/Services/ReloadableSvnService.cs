using SVNAssist.Models;

namespace SVNAssist.Services;

/// <summary>
/// <see cref="ISvnService"/> che ritrova <c>svn.exe</c> anche se viene installato dopo l'avvio.
/// </summary>
/// <remarks>
/// <see cref="SvnService"/> riceve il percorso di svn alla costruzione: se l'utente installa svn dalla
/// checklist dei prerequisiti mentre il pannello è aperto, senza questo wrapper servirebbe riavviare VS.
/// Finché svn non è stato trovato, ad ogni chiamata si ripete la ricerca (una scansione del PATH).
/// </remarks>
internal sealed class ReloadableSvnService : ISvnService
{
    private readonly Func<string?> _locate;
    private readonly Func<string, ISvnService> _create;
    private readonly object _gate = new();
    private ISvnService? _inner;
    private bool _located;

    public ReloadableSvnService(Func<string?>? locate = null, Func<string, ISvnService>? create = null)
    {
        _locate = locate ?? SvnLocator.FindSvn;
        _create = create ?? (path => new SvnService(path, new ProcessRunner()));
    }

    private ISvnService Current
    {
        get
        {
            lock (_gate)
            {
                if (!_located && _locate() is { } path)
                {
                    _inner = _create(path);
                    _located = true;
                }

                // Senza svn: percorso segnaposto, le operazioni falliscono in modo gestito.
                return _inner ??= _create("svn.exe");
            }
        }
    }

    public Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct) => Current.GetStatusAsync(workingCopyPath, ct);
    public Task<long?> CommitAsync(IEnumerable<string> paths, string message, CancellationToken ct, IEnumerable<string>? includeAllUnder = null) => Current.CommitAsync(paths, message, ct, includeAllUnder);
    public Task AddAsync(IEnumerable<string> paths, CancellationToken ct) => Current.AddAsync(paths, ct);
    public Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct, bool force = false) => Current.DeleteAsync(paths, ct, force);
    public Task RevertAsync(IEnumerable<string> paths, CancellationToken ct, bool recursive = true) => Current.RevertAsync(paths, ct, recursive);
    public Task ResolveAsync(string path, SvnConflictChoice accept, CancellationToken ct) => Current.ResolveAsync(path, accept, ct);
    public Task AddToIgnoreListAsync(string path, CancellationToken ct) => Current.AddToIgnoreListAsync(path, ct);
    public Task<UpdateResult> UpdateAsync(string workingCopyPath, CancellationToken ct) => Current.UpdateAsync(workingCopyPath, ct);
    public Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct) => Current.GetLogAsync(targetPath, maxEntries, ct);
    public Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct) => Current.GetDiffAsync(targetPath, revision, ct);
    public Task<string> GetDiffForAiAsync(IReadOnlyList<string> paths, string? workingCopyPath, CancellationToken ct) => Current.GetDiffForAiAsync(paths, workingCopyPath, ct);
    public Task<string> GetFileDiffAsync(string filePath, CancellationToken ct) => Current.GetFileDiffAsync(filePath, ct);
    public Task<bool> OpenNativeDiffAsync(string filePath, CancellationToken ct) => Current.OpenNativeDiffAsync(filePath, ct);
    public Task<string> GetBranchNameAsync(string workingCopyPath, CancellationToken ct) => Current.GetBranchNameAsync(workingCopyPath, ct);
}
