using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace SVNAssist.Rpc
{
    /// <summary>
    /// Visual Studio (e il backend figlio) hanno il PATH di quando sono partiti: gli installer aggiornano il registro, non i processi aperti.
    /// Questo helper rilegge il PATH da registro (macchina + utente) e lo unisce a quello del processo (file condiviso Host net472 / Backend / test).
    /// </summary>
    public static class PathRefresh
    {
        /// <summary>Logica pura: le voci di <paramref name="current"/> restano com'erano, quelle nuove di <paramref name="extras"/> vanno in coda; niente vuote né duplicate (case-insensitive, ignorando "\" finale).</summary>
        public static string Merge(string current, params string[] extras)
        {
            var lists = new List<string> { current };
            lists.AddRange(extras);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var list in lists)
                foreach (var raw in (list ?? "").Split(';'))
                {
                    var entry = raw.Trim();
                    var key = entry.Trim('"').TrimEnd('\\');
                    if (key.Length == 0 || !seen.Add(key)) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(entry);
                }
            return sb.ToString();
        }

        private static string ReadRegistryPath(RegistryKey root, string subKey)
        {
            try
            {
                using (var k = root.OpenSubKey(subKey))
                    return Environment.ExpandEnvironmentVariables((string)k?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "");
            }
            catch (Exception) { return ""; } // registro non leggibile: si resta col PATH del processo
        }

        /// <summary>Aggiorna il PATH del processo (e quindi dei figli) con quello del registro. <c>true</c> se è cambiato.</summary>
        public static bool Refresh()
        {
            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            var merged = Merge(current,
                ReadRegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
                ReadRegistryPath(Registry.CurrentUser, "Environment"));
            if (merged == current) return false;
            Environment.SetEnvironmentVariable("PATH", merged);
            return true;
        }
    }
}
