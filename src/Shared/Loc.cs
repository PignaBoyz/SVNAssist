// Localizzazione dei testi che vede l'utente (it/en). File condiviso: incluso da Host (net472) e Backend (net10).
// Convenzione "gettext": la CHIAVE e' il testo italiano (con segnaposto {0}, {1}...); LocEn.cs ha la traduzione inglese.
// In italiano T ritorna la chiave stessa. I testi per i log, i prompt dell'AI e i messaggi di svn.exe NON passano da qui.
#nullable enable
using System;
using System.Globalization;

namespace SVNAssist
{
    internal static partial class Loc
    {
        private static volatile string _lang = "it";

        /// <summary>Lingua effettiva dell'interfaccia: "it" o "en" (mai "auto"). Una per processo.</summary>
        public static string Lang
        {
            get { return _lang; }
            set { _lang = value == "en" ? "en" : "it"; }
        }

        /// <summary>Da impostazione (auto | it | en) e cultura UI di Visual Studio a "it" | "en". Auto = italiano se la cultura e' it-*.</summary>
        public static string Resolve(string? setting, string? cultureName)
        {
            if (setting == "it" || setting == "en") return setting;
            return cultureName != null && cultureName.StartsWith("it", StringComparison.OrdinalIgnoreCase) ? "it" : "en";
        }

        /// <summary>Marca una chiave che verra' tradotta piu' tardi con <see cref="T"/> (es. etichette di tabelle): ritorna la chiave.</summary>
        public static string K(string it) { return it; }

        /// <summary>Testo nella lingua corrente. <paramref name="it"/> e' letterale (la chiave); gli argomenti vanno in string.Format.</summary>
        public static string T(string it, params object?[]? args)
        {
            var s = it;
            if (_lang == "en" && En.TryGetValue(it, out var en)) s = en;
            return args == null || args.Length == 0 ? s : string.Format(CultureInfo.CurrentCulture, s, args);
        }
    }
}
