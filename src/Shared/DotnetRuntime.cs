using System;

namespace SVNAssist.Rpc
{
    /// <summary>Rilevamento del runtime .NET richiesto dal backend (file condiviso tra host net472 e test).</summary>
    public static class DotnetRuntime
    {
        public const int RequiredMajor = 10;

        /// <summary>
        /// Da un output di <c>dotnet --list-runtimes</c> ("Microsoft.NETCore.App 10.0.1 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]")
        /// ritorna la radice dotnet (da usare come DOTNET_ROOT) del runtime 10.x piu' recente, o null se manca.
        /// </summary>
        public static string FindRoot(string listRuntimesOutput)
        {
            string root = null; Version best = null;
            foreach (var raw in (listRuntimesOutput ?? "").Split('\n'))
            {
                var line = raw.Trim();
                const string name = "Microsoft.NETCore.App ";
                if (!line.StartsWith(name, StringComparison.Ordinal)) continue;
                var rest = line.Substring(name.Length);
                int sp = rest.IndexOf(' ');
                if (sp < 0) continue;
                var verText = rest.Substring(0, sp);
                int dash = verText.IndexOf('-'); // 10.0.0-preview.1
                if (dash >= 0) verText = verText.Substring(0, dash);
                Version v;
                if (!Version.TryParse(verText, out v) || v.Major != RequiredMajor) continue;
                int o = rest.IndexOf('['), c = rest.LastIndexOf(']');
                if (o < 0 || c <= o) continue;
                // [.../dotnet/shared/Microsoft.NETCore.App] -> .../dotnet
                var dir = rest.Substring(o + 1, c - o - 1).Trim().Replace('/', '\\');
                int i = dir.LastIndexOf("\\shared\\", StringComparison.OrdinalIgnoreCase);
                if (i <= 0) continue;
                if (best == null || v > best) { best = v; root = dir.Substring(0, i); }
            }
            return root;
        }
    }
}
