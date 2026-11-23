using System;
using System.Collections.Generic;
using System.Linq;

namespace SVNAssist.Navigation
{
    /// <summary>Navigazione tra blocchi modificati (logica pura, condivisa host/test): sceglie la riga di destinazione.</summary>
    public static class DiffNav
    {
        /// <summary>Prima riga di blocco dopo <paramref name="current"/>; oltre l'ultimo, giro al primo. 0 se non ci sono blocchi.</summary>
        public static int Next(IReadOnlyList<int> starts, int current)
        {
            if (starts == null || starts.Count == 0) return 0;
            int best = 0, first = int.MaxValue;
            foreach (var s in starts)
            {
                if (s < first) first = s;
                if (s > current && (best == 0 || s < best)) best = s;
            }
            return best != 0 ? best : first;
        }

        /// <summary>Ultima riga di blocco prima di <paramref name="current"/>; prima del primo, giro all'ultimo. 0 se non ci sono blocchi.</summary>
        public static int Previous(IReadOnlyList<int> starts, int current)
        {
            if (starts == null || starts.Count == 0) return 0;
            int best = 0, last = 0;
            foreach (var s in starts)
            {
                if (s > last) last = s;
                if (s < current && s > best) best = s;
            }
            return best != 0 ? best : last;
        }
    }

    /// <summary>Normalizzazione e confronto scorciatoie EnvDTE (<c>Scope::Mod+Tasto</c>, indipendente dall'ordine modificatori).</summary>
    public static class ShortcutBinding
    {
        private static string StripScope(string? binding, out string? scopePrefix)
        {
            var s = (binding ?? "").Trim();
            var idx = s.IndexOf("::", StringComparison.Ordinal);
            if (idx >= 0)
            {
                scopePrefix = s.Substring(0, idx + 2);
                return s.Substring(idx + 2).Trim();
            }
            scopePrefix = null;
            return s;
        }

        /// <summary>Normalizza una combinazione (es. <c>Global::Shift+Alt+F8</c> e <c>Alt+Shift+F8</c> diventano <c>shift+alt+f8</c>).</summary>
        public static string NormalizeKeyCombo(string? binding)
        {
            var combo = StripScope(binding, out _);
            if (combo.Length == 0) return "";
            var parts = combo.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().ToLowerInvariant())
                .Where(p => p.Length > 0)
                .ToList();
            if (parts.Count == 0) return "";

            var mods = new HashSet<string>(StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (var p in parts)
            {
                var normMod = p == "control" ? "ctrl" : p;
                if (normMod == "ctrl" || normMod == "shift" || normMod == "alt")
                    mods.Add(normMod);
                else
                    keys.Add(p);
            }

            var ordered = new List<string>(4);
            if (mods.Contains("ctrl")) ordered.Add("Ctrl");
            if (mods.Contains("shift")) ordered.Add("Shift");
            if (mods.Contains("alt")) ordered.Add("Alt");
            ordered.AddRange(keys);
            return string.Join("+", ordered).ToLowerInvariant();
        }

        /// <summary>Restituisce la combinazione con ordine canonico dei modificatori (Ctrl+Shift+Alt+Tasto), preservando eventuale scope.</summary>
        public static string Canonicalize(string? keys)
        {
            var raw = StripScope(keys, out var scope);
            if (raw.Length == 0) return "";
            var parts = raw.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mainKeys = new List<string>();
            foreach (var p in parts)
            {
                var lower = p.ToLowerInvariant();
                var m = lower == "control" ? "ctrl" : lower;
                if (m == "ctrl" || m == "shift" || m == "alt") mods.Add(m);
                else mainKeys.Add(p);
            }
            var ordered = new List<string>(4);
            if (mods.Contains("ctrl")) ordered.Add("Ctrl");
            if (mods.Contains("shift")) ordered.Add("Shift");
            if (mods.Contains("alt")) ordered.Add("Alt");
            ordered.AddRange(mainKeys);
            var combo = string.Join("+", ordered);
            return scope != null ? scope + combo : combo;
        }

        /// <summary>Vero se almeno un binding esistente equivale alla combinazione desiderata.</summary>
        public static bool MatchesAny(IEnumerable<string>? existingBindings, string? desired)
        {
            var want = NormalizeKeyCombo(desired);
            if (want.Length == 0 || existingBindings == null) return false;
            foreach (var b in existingBindings)
            {
                if (string.Equals(NormalizeKeyCombo(b), want, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>Estrae il prefisso <c>Scope::</c> dal primo binding valido (es. <c>Global::</c> o <c>Globale::</c>), altrimenti <c>Global::</c>.</summary>
        public static string ExtractScopePrefix(IEnumerable<string>? existingBindings)
        {
            if (existingBindings != null)
            {
                foreach (var b in existingBindings)
                {
                    StripScope(b, out var prefix);
                    if (!string.IsNullOrEmpty(prefix))
                        return prefix!;
                }
            }
            return "Global::";
        }

        /// <summary>Aggiunge <paramref name="scopePrefix"/> a <paramref name="keys"/> solo se non specifica gia' uno scope (<c>::</c>).</summary>
        public static string WithScope(string scopePrefix, string? keys)
        {
            var k = (keys ?? "").Trim();
            if (k.Length == 0) return "";
            if (k.IndexOf("::", StringComparison.Ordinal) >= 0) return k;
            return (string.IsNullOrEmpty(scopePrefix) ? "Global::" : scopePrefix) + k;
        }
    }
}
