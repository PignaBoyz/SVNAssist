using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using ISettingsManager = Microsoft.VisualStudio.Utilities.UnifiedSettings.ISettingsManager;

namespace SVNAssist.Host
{
    // Servizio di Unified Settings: il tipo vive in un assembly di interop interno, si ridichiara con lo stesso GUID
    // (GetServiceAsync risolve per GUID del tipo). Stessa tecnica di altre estensioni VSSDK per VS 2026.
    [Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
    internal interface SVsUnifiedSettingsManager
    {
    }

    /// <summary>
    /// Opzioni moderne di Visual Studio 2026 (Tools > Options > SVNAssist, dichiarate in Options/registration.json): niente
    /// popup della vecchia pagina. <see cref="SvnAssistOptions"/> resta il modello in memoria usato da tutto l'host e dal backend
    /// (e il posto delle chiavi API e del token Azure, cifrati, che si inseriscono nelle procedure guidate del pannello):
    /// questa classe ci copia i valori di Unified Settings (<see cref="ReadInto"/>, all'avvio e quando VS torna in primo piano)
    /// e scrive in Unified Settings quello che si cambia dal pannello (<see cref="Write"/>). Al primo avvio copia una volta i
    /// valori della vecchia pagina (<see cref="MigrateOnce"/>). Se Unified Settings non c'e' (modalita' classica), resta tutto come prima.
    /// </summary>
    internal sealed class UnifiedOptions
    {
        internal enum Kind { Bool, Int, Text }

        /// <summary>Chiave (come in <see cref="SvnAssistOptions.ToDictionary"/> / TrySet) -> moniker, tipo e default di registration.json.</summary>
        internal static readonly (string Key, string Moniker, Kind Kind, string Default)[] Map =
        {
            ("uiLanguage", "svnAssist.general.uiLanguage", Kind.Text, "auto"),
            ("windowTheme", "svnAssist.general.windowTheme", Kind.Text, "auto"),
            ("logTheme", "svnAssist.general.logTheme", Kind.Text, "same"),
            ("showSccGlyphs", "svnAssist.general.showSccGlyphs", Kind.Bool, "true"),
            ("lightweightMode", "svnAssist.general.lightweightMode", Kind.Bool, "false"),
            ("headCheckMinutes", "svnAssist.general.headCheckMinutes", Kind.Int, "5"),
            ("workingCopyPath", "svnAssist.general.workingCopyPath", Kind.Text, ""),
            ("repoLayout", "svnAssist.general.repoLayout", Kind.Text, "trunk/branches/tags"),
            ("aiProvider", "svnAssist.ai.aiProvider", Kind.Text, "auto"),
            ("defaultLanguage", "svnAssist.ai.defaultLanguage", Kind.Text, "auto"),
            ("aiShowUsage", "svnAssist.ai.aiShowUsage", Kind.Bool, "false"),
            ("aiExplainIncoming", "svnAssist.ai.aiExplainIncoming", Kind.Bool, "false"),
            ("claudeModel", "svnAssist.ai.claudeModel", Kind.Text, "claude-sonnet-5-5"),
            ("copilotModel", "svnAssist.ai.copilotModel", Kind.Text, "default"),
            ("aiEndpoint", "svnAssist.ai.aiEndpoint", Kind.Text, "https://models.github.ai/inference/chat/completions"),
            ("aiModel", "svnAssist.ai.aiModel", Kind.Text, "openai/gpt-4o-mini"),
            ("geminiModel", "svnAssist.ai.geminiModel", Kind.Text, "gemini-2.5-flash"),
            ("nextChangeShortcut", "svnAssist.shortcuts.nextChangeShortcut", Kind.Text, "Alt+F8"),
            ("previousChangeShortcut", "svnAssist.shortcuts.previousChangeShortcut", Kind.Text, "Shift+Alt+F8"),
            ("azEnabled", "svnAssist.azure.azEnabled", Kind.Bool, "false"),
            ("azOrg", "svnAssist.azure.azOrg", Kind.Text, ""),
            ("azProject", "svnAssist.azure.azProject", Kind.Text, ""),
            ("azLinkOnCommit", "svnAssist.azure.azLinkOnCommit", Kind.Bool, "true"),
            ("azComment", "svnAssist.azure.azComment", Kind.Bool, "false"),
            ("azAuth", "svnAssist.azure.azAuth", Kind.Text, "pat"),
            ("azClientId", "svnAssist.azure.azClientId", Kind.Text, ""),
            ("azTenant", "svnAssist.azure.azTenant", Kind.Text, ""),
        };

        private const string StateCollection = "SVNAssist";
        private const string MigratedFlag = "UnifiedSettingsMigrated";

        private readonly ISettingsManager _unified;
        private readonly WritableSettingsStore _store;

        private UnifiedOptions(ISettingsManager unified, WritableSettingsStore store)
        {
            _unified = unified;
            _store = store;
        }

        /// <summary>null se Unified Settings non e' disponibile: l'host continua con i soli valori salvati da SvnAssistOptions.</summary>
        internal static async Task<UnifiedOptions> CreateAsync(AsyncPackage package)
        {
            ISettingsManager unified = null;
            try { unified = await package.GetServiceAsync(typeof(SVsUnifiedSettingsManager)) as ISettingsManager; }
            catch (Exception ex) { Log.Write("Unified Settings non disponibile: " + ex.Message); }
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (unified == null) return null;
            return new UnifiedOptions(unified, new ShellSettingsManager(package).GetWritableSettingsStore(SettingsScope.UserSettings));
        }

        /// <summary>Valore di <see cref="SvnAssistOptions.ToDictionary"/> nel formato di TrySet ("true"/"false", numeri invarianti).</summary>
        private static string Current(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v ?? "" : "";

        /// <summary>
        /// Copia i valori di Unified Settings nelle opzioni in memoria. true se qualcosa e' cambiato (allora il chiamante
        /// salva e avvisa: lingua, tema, backend).
        /// </summary>
        internal bool ReadInto(SvnAssistOptions o)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ISettingsReader reader;
            try { reader = _unified.GetReader(); } catch (Exception ex) { Log.Write("Unified Settings, lettura: " + ex.Message); return false; }
            var now = o.ToDictionary();
            now["showSccGlyphs"] = o.ShowSccGlyphs ? "true" : "false";
            now["nextChangeShortcut"] = o.NextChangeShortcut ?? "";
            now["previousChangeShortcut"] = o.PreviousChangeShortcut ?? "";
            var changed = false;
            foreach (var m in Map)
            {
                string value;
                try
                {
                    switch (m.Kind)
                    {
                        case Kind.Bool:
                            {
                                var r = reader.GetValue<bool>(m.Moniker);
                                if (r.Outcome != SettingRetrievalOutcome.Success) continue;
                                value = r.Value ? "true" : "false";
                                break;
                            }
                        case Kind.Int:
                            {
                                var r = reader.GetValue<int>(m.Moniker);
                                if (r.Outcome != SettingRetrievalOutcome.Success) continue;
                                value = r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                break;
                            }
                        default:
                            {
                                var r = reader.GetValue<string>(m.Moniker);
                                if (r.Outcome != SettingRetrievalOutcome.Success) continue;
                                value = r.Value ?? "";
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Unified Settings, " + m.Moniker + ": " + ex.Message);
                    continue;
                }

                if (!string.Equals(Current(now, m.Key), value, StringComparison.Ordinal) && o.TrySet(m.Key, value))
                    changed = true;
            }
            return changed;
        }

        /// <summary>Una modifica fatta dal pannello (menu Tema, procedure guidate...) va anche in Unified Settings, cosi' Tools > Options la mostra.</summary>
        internal void Write(string key, string value)
        {
            var m = Map.FirstOrDefault(x => x.Key == key);
            if (m.Key == null) return; // chiave non in Unified Settings (es. segreti): resta solo in SvnAssistOptions
            try
            {
                var writer = _unified.GetWriter("SVNAssist");
                var outcome = Enqueue(writer, m, value);
                if (outcome == SettingChangeOutcome.PendingCommit || outcome == SettingChangeOutcome.PendingCommitWithoutValidation)
                    writer.RequestCommit("SVNAssist: " + key);
            }
            catch (Exception ex) { Log.Write("Unified Settings, scrittura " + key + ": " + ex.Message); }
        }

        private static SettingChangeOutcome Enqueue(ISettingsWriter writer, (string Key, string Moniker, Kind Kind, string Default) m, string value)
        {
            switch (m.Kind)
            {
                case Kind.Bool: return writer.EnqueueChange(m.Moniker, string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)).Outcome;
                case Kind.Int: return writer.EnqueueChange(m.Moniker, int.TryParse(value, out var n) ? n : int.Parse(m.Default)).Outcome;
                default: return writer.EnqueueChange(m.Moniker, value ?? "").Outcome;
            }
        }

        /// <summary>
        /// Primo avvio con le opzioni moderne: copia una volta i valori della vecchia pagina (registro di SvnAssistOptions) che non sono
        /// quelli di default. Poi comanda Unified Settings.
        /// </summary>
        internal void MigrateOnce(SvnAssistOptions o)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_store.PropertyExists(StateCollection, MigratedFlag)) return;
            var now = o.ToDictionary();
            now["showSccGlyphs"] = o.ShowSccGlyphs ? "true" : "false";
            now["nextChangeShortcut"] = o.NextChangeShortcut ?? "";
            now["previousChangeShortcut"] = o.PreviousChangeShortcut ?? "";
            try
            {
                var writer = _unified.GetWriter("SVNAssist");
                var queued = 0;
                foreach (var m in Map)
                {
                    var value = Current(now, m.Key);
                    if (string.Equals(value, m.Default, StringComparison.Ordinal)) continue;
                    var outcome = Enqueue(writer, m, value);
                    if (outcome == SettingChangeOutcome.NotSupportedInClassicMode) return; // si riprova quando Unified Settings e' attivo
                    if (outcome == SettingChangeOutcome.PendingCommit || outcome == SettingChangeOutcome.PendingCommitWithoutValidation) queued++;
                    else Log.Write("Unified Settings, migrazione " + m.Key + ": " + outcome);
                }
                if (queued > 0)
                {
                    var commit = writer.RequestCommit("SVNAssist: opzioni portate nella nuova pagina");
                    if (commit.Outcome != SettingCommitOutcome.Success && commit.Outcome != SettingCommitOutcome.PendingApproval)
                    {
                        Log.Write("Unified Settings, migrazione non riuscita: " + commit.Outcome);
                        return;
                    }
                }
                if (!_store.CollectionExists(StateCollection)) _store.CreateCollection(StateCollection);
                _store.SetBoolean(StateCollection, MigratedFlag, true);
                Log.Write("Unified Settings: migrate " + queued + " opzioni dalla vecchia pagina.");
            }
            catch (Exception ex) { Log.Write("Unified Settings, migrazione: " + ex.Message); }
        }
    }
}
