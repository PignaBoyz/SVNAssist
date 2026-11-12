namespace SVNAssist.Models;

/// <summary>Esito di <c>svn info --xml</c> sulla radice della working copy.</summary>
/// <param name="Url">URL completo della working copy (come lo scrive svn, con i caratteri codificati).</param>
/// <param name="RelativeUrl">Percorso dalla radice del repository, nella forma <c>^/trunk</c> (<c>^/</c> = radice).</param>
/// <param name="RepositoryRoot">URL della radice del repository.</param>
/// <param name="Revision">Revisione della working copy.</param>
public sealed record SvnInfo(
    string Url,
    string RelativeUrl,
    string RepositoryRoot,
    long Revision,
    long? LastChangedRev,
    string? LastChangedAuthor,
    DateTimeOffset? LastChangedDate);

/// <summary>Dove sta la working copy rispetto al layout: <see cref="Kind"/> = trunk | branch | tag | other.</summary>
/// <param name="Base">Percorso (dalla radice del repository) che contiene trunk/branches/tags; vuoto = radice.</param>
/// <param name="Name">Nome del ramo (<c>feature-x</c>), vuoto per trunk/other.</param>
/// <param name="Label">Testo da mostrare: <c>trunk</c>, <c>branches/feature-x</c>, <c>tags/1.0</c>, il percorso, oppure "/ (radice)".</param>
public sealed record RepoPosition(string Kind, string Name, string Base, string Label);

/// <summary>Nomi delle cartelle standard del repository (impostazione <c>repoLayout</c>, default <c>trunk/branches/tags</c>).</summary>
public sealed record RepoLayout(string Trunk, string Branches, string Tags)
{
    public const string Default = "trunk/branches/tags";

    /// <summary>
    /// Legge "trunk/branches/tags" (separatori <c>/</c>, <c>,</c> o <c>;</c>). Se non sono tre nomi non vuoti ricade sul default.
    /// shortcut: ogni nome e' una sola cartella (niente "a/b"), basta per i layout reali; da estendere se serve.
    /// </summary>
    public static RepoLayout Parse(string? setting)
    {
        var p = (setting ?? "").Split(['/', ',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 3 ? new RepoLayout(p[0], p[1], p[2]) : new RepoLayout("trunk", "branches", "tags");
    }

    /// <summary>
    /// Deduce il ramo corrente dall'URL relativo (<c>^/progetto/branches/x/src</c>). Cerca da sinistra la prima cartella
    /// trunk/branches/tags: quello che sta prima e' la base (<c>progetto</c>). Senza corrispondenza (progetto alla radice, o
    /// repository senza layout standard) la base e' la radice.
    /// </summary>
    public RepoPosition Deduce(string relativeUrl)
    {
        var segs = Segments(relativeUrl);
        for (var i = 0; i < segs.Length; i++)
        {
            var basePath = string.Join("/", segs.Take(i));
            if (segs[i] == Trunk)
                return new RepoPosition("trunk", "", basePath, Trunk);
            if (i + 1 < segs.Length && segs[i] == Branches)
                return new RepoPosition("branch", segs[i + 1], basePath, Branches + "/" + segs[i + 1]);
            if (i + 1 < segs.Length && segs[i] == Tags)
                return new RepoPosition("tag", segs[i + 1], basePath, Tags + "/" + segs[i + 1]);
        }

        return new RepoPosition("other", "", "", segs.Length == 0 ? "/ (radice)" : string.Join("/", segs));
    }

    internal static string[] Segments(string relativeUrl)
        => Uri.UnescapeDataString(relativeUrl.Trim()).TrimStart('^').Split('/', StringSplitOptions.RemoveEmptyEntries);
}
