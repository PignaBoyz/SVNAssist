using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SVNAssist.Host
{
    /// <summary>Tendina per una proprieta' stringa: valore memorizzato (chiave del setting) e testo mostrato.</summary>
    internal abstract class ChoiceConverter : StringConverter
    {
        private readonly (string Value, string Label)[] _choices;
        protected ChoiceConverter(params (string, string)[] choices) { _choices = choices; }

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c) =>
            new StandardValuesCollection(_choices.Select(x => x.Value).ToArray());

        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is string s)
                return _choices.Where(x => x.Value == s).Select(x => x.Label).FirstOrDefault() ?? s;
            return base.ConvertTo(c, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo culture, object value) =>
            value is string s ? _choices.Where(x => x.Label == s).Select(x => x.Value).FirstOrDefault() ?? s : base.ConvertFrom(c, culture, value);
    }

    internal sealed class AiProviderConverter : ChoiceConverter
    {
        public AiProviderConverter() : base(("auto", "Automatico (consigliato)"), ("copilot", "GitHub Copilot (abbonamento)"),
            ("claudecode", "Claude Code (abbonamento Claude)"), ("claude", "Claude API (API key Anthropic)"), ("openai", "OpenAI-compatibile / GitHub Models")) { }
    }

    internal sealed class ClaudeModelConverter : ChoiceConverter
    {
        public ClaudeModelConverter() : base(("claude-sonnet-5-5", "Sonnet (consigliato, equilibrato)"), ("claude-haiku-5-5", "Haiku (il più economico e veloce)"),
            ("claude-opus-5-5", "Opus (il più potente, costoso)")) { }
    }

    internal sealed class CopilotModelConverter : ChoiceConverter
    {
        public CopilotModelConverter() : base(("default", "Predefinito di Copilot (consigliato)"), ("gpt-4.1", "GPT-4.1"),
            ("gpt-5-mini", "GPT-5 mini"), ("claude-sonnet-4.5", "Claude Sonnet 4.5")) { }
    }

    internal sealed class AiModelConverter : ChoiceConverter
    {
        public AiModelConverter() : base(("openai/gpt-4o-mini", "GPT-4o mini (consigliato, gratuito su GitHub Models)"), ("openai/gpt-4o", "GPT-4o"),
            ("openai/gpt-4.1-mini", "GPT-4.1 mini"), ("openai/gpt-4.1", "GPT-4.1")) { }
    }

    internal sealed class LanguageConverter : ChoiceConverter
    {
        public LanguageConverter() : base(("auto", "Automatica"), ("italiano", "Italiano"), ("english", "English")) { }
    }

    internal sealed class WindowThemeConverter : ChoiceConverter
    {
        public WindowThemeConverter() : base(("auto", "Automatico (segue Visual Studio)"), ("light", "Chiaro"), ("dark", "Scuro"), ("blue", "Blu")) { }
    }

    /// <summary>
    /// Tools > Options > SVNAssist. Stesse impostazioni (e default) di SettingsService/SettingDefinitions della 0.1.0,
    /// piu' windowTheme. VS le salva da solo nel registro (DialogPage).
    /// </summary>
    [Guid(Ids.OptionsPageGuid)]
    public sealed class SvnAssistOptions : DialogPage
    {
        internal const string GitHubModelsEndpoint = "https://models.github.ai/inference/chat/completions";
        internal const string LegacyGitHubModelsEndpoint = "https://models.inference.ai.azure.com/chat/completions";

        // chiave del setting (come in SettingsService, camelCase) -> proprieta'
        internal static readonly string[] Keys =
            { "aiProvider", "claudeModel", "anthropicApiKey", "copilotModel", "aiEndpoint", "aiApiKey", "aiModel", "defaultLanguage", "workingCopyPath", "windowTheme" };

        private const string Ai = "Commit message AI";

        [Category(Ai), DisplayName("Provider AI per i commit message"), DefaultValue("auto"), TypeConverter(typeof(AiProviderConverter))]
        [Description("Automatico prova Copilot, Claude Code, Claude API e OpenAI-compatibile in quest'ordine.")]
        public string AiProvider { get; set; } = "auto";

        [Category(Ai), DisplayName("Modello Claude (solo Claude Code e Claude API)"), DefaultValue("claude-sonnet-5-5"), TypeConverter(typeof(ClaudeModelConverter))]
        public string ClaudeModel { get; set; } = "claude-sonnet-5-5";

        [Category(Ai), DisplayName("API key Anthropic (solo Claude API)"), PasswordPropertyText(true)]
        [Description("Vuota = variabile d'ambiente ANTHROPIC_API_KEY. Salvata cifrata (DPAPI, solo per il tuo utente Windows) nelle impostazioni di Visual Studio.")]
        public string AnthropicApiKey { get; set; } = "";

        [Category(Ai), DisplayName("Modello Copilot (solo Copilot)"), DefaultValue("default"), TypeConverter(typeof(CopilotModelConverter))]
        [Description("Serve un abbonamento Copilot e 'gh auth login'.")]
        public string CopilotModel { get; set; } = "default";

        [Category(Ai), DisplayName("URL endpoint (solo OpenAI-compatibile)"), DefaultValue(GitHubModelsEndpoint)]
        [Description("Endpoint Chat Completions compatibile OpenAI. Default: GitHub Models.")]
        public string AiEndpoint { get; set; } = GitHubModelsEndpoint;

        [Category(Ai), DisplayName("API key o GitHub PAT (solo OpenAI-compatibile)"), PasswordPropertyText(true)]
        [Description("Salvata cifrata (DPAPI, solo per il tuo utente Windows) nelle impostazioni di Visual Studio.")]
        public string AiApiKey { get; set; } = "";

        [Category(Ai), DisplayName("Modello (solo OpenAI-compatibile / GitHub Models)"), DefaultValue("openai/gpt-4o-mini"), TypeConverter(typeof(AiModelConverter))]
        public string AiModel { get; set; } = "openai/gpt-4o-mini";

        [Category(Ai), DisplayName("Lingua dei commit message"), DefaultValue("auto"), TypeConverter(typeof(LanguageConverter))]
        public string DefaultLanguage { get; set; } = "auto";

        [Category("Generale"), DisplayName("Working Copy Path")]
        [Description("Se impostato ha la precedenza sull'auto-rilevamento dalla solution aperta.")]
        public string WorkingCopyPath { get; set; } = "";

        [Category("Generale"), DisplayName("Tema delle finestre SVNAssist"), DefaultValue("auto"), TypeConverter(typeof(WindowThemeConverter))]
        [Description("Automatico usa i colori del tema di Visual Studio (chiaro, scuro, blu, alto contrasto).")]
        public string WindowTheme { get; set; } = "auto";

        // Le API key finiscono nel registro cifrate con DPAPI (utente corrente), non in chiaro. Un valore salvato
        // in chiaro da versioni precedenti si legge com'e' e viene cifrato al prossimo salvataggio.
        private const string Prefix = "dpapi:";

        public override void SaveSettingsToStorage()
        {
            var a = AnthropicApiKey; var k = AiApiKey;
            try { AnthropicApiKey = Protect(a); AiApiKey = Protect(k); base.SaveSettingsToStorage(); }
            finally { AnthropicApiKey = a; AiApiKey = k; }
        }

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            AnthropicApiKey = Unprotect(AnthropicApiKey);
            AiApiKey = Unprotect(AiApiKey);
        }

        private static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var enc = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(s), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(enc);
        }

        private static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith(Prefix, StringComparison.Ordinal)) return s ?? "";
            try
            {
                var raw = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(s.Substring(Prefix.Length)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(raw);
            }
            catch { return ""; } // altro utente/macchina: la chiave va reinserita
        }

        /// <summary>Notifica "impostazioni cambiate" (dopo OK in Options o SetSetting).</summary>
        internal static event Action Changed;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply) Changed?.Invoke();
        }

        /// <summary>Valori correnti come chiave -> stringa; i valori fuori lista ricadono sul default.</summary>
        internal Dictionary<string, string> ToDictionary()
        {
            var endpoint = (AiEndpoint ?? "").Trim();
            if (string.Equals(endpoint, LegacyGitHubModelsEndpoint, StringComparison.OrdinalIgnoreCase)) endpoint = GitHubModelsEndpoint;
            if (endpoint.Length == 0) endpoint = GitHubModelsEndpoint;
            var d = new Dictionary<string, string>
            {
                ["aiProvider"] = Pick(AiProvider, new AiProviderConverter(), "auto"),
                ["claudeModel"] = Pick(ClaudeModel, new ClaudeModelConverter(), "claude-sonnet-5-5"),
                ["anthropicApiKey"] = AnthropicApiKey ?? "",
                ["copilotModel"] = Pick(CopilotModel, new CopilotModelConverter(), "default"),
                ["aiEndpoint"] = endpoint,
                ["aiApiKey"] = AiApiKey ?? "",
                ["aiModel"] = Pick(AiModel, new AiModelConverter(), "openai/gpt-4o-mini"),
                ["defaultLanguage"] = Pick(DefaultLanguage, new LanguageConverter(), "auto"),
                ["workingCopyPath"] = WorkingCopyPath ?? "",
                ["windowTheme"] = Pick(WindowTheme, new WindowThemeConverter(), "auto"),
            };
            // Alias minuscoli: sono gli id dei setting della 0.1.0 (SettingDefinitions), per chi li usa ancora.
            foreach (var k in Keys.ToList()) d[k.ToLowerInvariant()] = d[k];
            return d;
        }

        private static string Pick(string value, ChoiceConverter conv, string fallback)
        {
            var known = conv.GetStandardValues(null).Cast<string>();
            return known.Contains(value) ? value : fallback;
        }

        /// <summary>Imposta una chiave (camelCase); false se la chiave non esiste.</summary>
        internal bool TrySet(string key, string value)
        {
            switch (key)
            {
                case "aiProvider": AiProvider = value; break;
                case "claudeModel": ClaudeModel = value; break;
                case "anthropicApiKey": AnthropicApiKey = value; break;
                case "copilotModel": CopilotModel = value; break;
                case "aiEndpoint": AiEndpoint = value; break;
                case "aiApiKey": AiApiKey = value; break;
                case "aiModel": AiModel = value; break;
                case "defaultLanguage": DefaultLanguage = value; break;
                case "workingCopyPath": WorkingCopyPath = value; break;
                case "windowTheme": WindowTheme = value; break;
                default: return false;
            }
            return true;
        }
    }
}
