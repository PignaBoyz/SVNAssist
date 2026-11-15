using SVNAssist.Models;
using SVNAssist.Stores;

namespace SVNAssist.Services;

/// <summary>
/// Layout del repository effettivo: quello delle Options (<c>repoLayout</c>); se l'URL della working copy non vi corrisponde prova a dedurlo
/// con <c>svn list</c> sul percorso (radice compresa) cercando le cartelle trunk/branches/tags (anche main, branch, tag...). L'esito sta in cache per sessione.
/// </summary>
internal sealed class RepoLayoutResolver(ISvnService svn, ISvnAssistSettings settings)
{
    private static readonly string[] TrunkNames = ["trunk", "main", "master"], BranchNames = ["branches", "branch"], TagNames = ["tags", "tag"];
    private readonly Dictionary<string, RepoLayout> _cache = [];

    public async Task<RepoLayout> ResolveAsync(SvnInfo info, CancellationToken ct)
    {
        var setting = await settings.GetRepoLayoutAsync(ct);
        var configured = RepoLayout.Parse(setting);
        if (configured.Deduce(info.RelativeUrl).Kind != "other")
            return configured;
        var key = setting + "|" + info.RepositoryRoot + "|" + info.RelativeUrl;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit))
                return hit;
        }

        var found = configured;
        var segs = RepoLayout.Segments(info.RelativeUrl);
        // shortcut: al massimo 6 livelli di svn list, basta per i repository reali (progetto/sotto-progetto/trunk).
        for (var i = 0; i <= Math.Min(segs.Length, 5) && ReferenceEquals(found, configured); i++)
        {
            IReadOnlyList<SvnListEntry> entries;
            try { entries = await svn.ListEntriesAsync(SwitchPickerStore.UrlOf(info.RepositoryRoot.TrimEnd('/'), string.Join("/", segs.Take(i))), ct); }
            catch (InvalidOperationException) { continue; }
            string? Pick(string[] names) => entries.Select(e => e.Name).FirstOrDefault(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
            string?[] p = [Pick(TrunkNames), Pick(BranchNames), Pick(TagNames)];
            if (p.Count(x => x is not null) < 2)
                continue;
            var candidate = new RepoLayout(p[0] ?? configured.Trunk, p[1] ?? configured.Branches, p[2] ?? configured.Tags);
            if (candidate.Deduce(info.RelativeUrl).Kind != "other")
                found = candidate;
        }

        lock (_cache)
            return _cache[key] = found;
    }
}
