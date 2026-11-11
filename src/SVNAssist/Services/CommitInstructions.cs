using System.IO;
using System.Text;

namespace SVNAssist.Services;

/// <summary>
/// Cerca le regole di commit scritte dall'utente/progetto e le prepara per il prompt di sistema dell'AI.
/// </summary>
/// <remarks>
/// Fonti, dalla più generale alla più specifica (le più specifiche stanno in fondo e prevalgono):
/// 1. <c>~/.copilot/copilot-instructions.md</c> (file utente globale, lo stesso della Copilot CLI)
/// 2. risalendo dalla working copy, la prima cartella con <c>.github/copilot-instructions.md</c>
///    e/o <c>.github/instructions/*.instructions.md</c> con <c>applyTo</c> globale (<c>**</c>, <c>*</c> o assente).
/// Il contenuto è limitato in dimensione: va nel prompt di ogni richiesta.
/// </remarks>
internal static class CommitInstructions
{
    internal const int MaxFileChars = 4000;
    internal const int MaxTotalChars = 8000;
    private const int MaxParentLevels = 12;

    /// <summary>Legge le regole applicabili alla working copy; <c>null</c> se non ce ne sono.</summary>
    /// <param name="userHome">Cartella utente (default: profilo corrente); iniettabile per i test.</param>
    internal static string? Load(string? workingCopyPath, string? userHome = null)
    {
        var parts = new List<string>();
        try
        {
            userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userHome))
                AddFile(parts, Path.Combine(userHome, ".copilot", "copilot-instructions.md"));

            var dir = string.IsNullOrWhiteSpace(workingCopyPath) ? null : new DirectoryInfo(workingCopyPath);
            for (var level = 0; dir is not null && level < MaxParentLevels; level++, dir = dir.Parent)
            {
                var github = Path.Combine(dir.FullName, ".github");
                var before = parts.Count;
                AddFile(parts, Path.Combine(github, "copilot-instructions.md"));

                var folder = Path.Combine(github, "instructions");
                if (Directory.Exists(folder))
                    foreach (var file in Directory.EnumerateFiles(folder, "*.instructions.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        AddFile(parts, file, requireGlobalApplyTo: true);

                if (parts.Count > before)
                    break; // la cartella più vicina alla working copy vince
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Regole illeggibili: si genera comunque senza.
        }

        if (parts.Count == 0)
            return null;

        var all = string.Join("\n\n", parts);
        return all.Length <= MaxTotalChars ? all : all[..MaxTotalChars] + "\n...(troncato)";
    }

    /// <summary>Blocco da aggiungere al prompt di sistema (vuoto se non ci sono regole).</summary>
    internal static string ForPrompt(string? instructions)
        => string.IsNullOrWhiteSpace(instructions)
            ? string.Empty
            : "\n\nRegole del progetto/utente: rispetta quelle che riguardano i messaggi di commit " +
              "(formato, lingua, prefissi, lunghezza), ignora il resto.\n---\n" + instructions.Trim() + "\n---";

    private static void AddFile(List<string> parts, string path, bool requireGlobalApplyTo = false)
    {
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path, Encoding.UTF8);
        var body = StripFrontMatter(text, out var applyTo);
        if (requireGlobalApplyTo && applyTo is not null && applyTo is not ("**" or "*" or "**/*"))
            return;

        body = body.Trim();
        if (body.Length == 0)
            return;

        parts.Add(body.Length <= MaxFileChars ? body : body[..MaxFileChars] + "\n...(troncato)");
    }

    /// <summary>Toglie il blocco <c>---...---</c> iniziale e ne estrae <c>applyTo</c> (senza virgolette).</summary>
    internal static string StripFrontMatter(string text, out string? applyTo)
    {
        applyTo = null;
        text = text.TrimStart('﻿');
        if (!text.StartsWith("---", StringComparison.Ordinal))
            return text;

        var lines = text.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "---")
                return string.Join("\n", lines.Skip(i + 1));
            if (line.StartsWith("applyTo:", StringComparison.OrdinalIgnoreCase))
                applyTo = line["applyTo:".Length..].Trim().Trim('"', '\'');
        }

        return text; // front matter mai chiuso: non è un front matter
    }
}
