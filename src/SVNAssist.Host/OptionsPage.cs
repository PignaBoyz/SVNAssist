using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SVNAssist.Host
{
    // Testi della pagina Options nella lingua corrente (Loc.T e' valutato a ogni lettura: cambiano al volo,
    // salvo la Category che il PropertyGrid memorizza alla prima lettura: segue la lingua dopo un riavvio di VS).
    internal sealed class LDisplayNameAttribute : DisplayNameAttribute
    {
        private readonly string _key;
        public LDisplayNameAttribute(string key) : base(key) { _key = key; }
        public override string DisplayName { get { return Loc.T(_key); } }
    }

    internal sealed class LDescriptionAttribute : DescriptionAttribute
    {
        private readonly string _key;
        public LDescriptionAttribute(string key) : base(key) { _key = key; }
        public override string Description { get { return Loc.T(_key); } }
    }

    internal sealed class LCategoryAttribute : CategoryAttribute
    {
        public LCategoryAttribute(string key) : base(key) { }
        protected override string GetLocalizedString(string value) { return Loc.T(value); }
    }

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
                return _choices.Where(x => x.Value == s).Select(x => Loc.T(x.Label)).FirstOrDefault() ?? s;
            return base.ConvertTo(c, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo culture, object value) =>
            value is string s ? _choices.Where(x => Loc.T(x.Label) == s || x.Label == s).Select(x => x.Value).FirstOrDefault() ?? s : base.ConvertFrom(c, culture, value);
    }

    internal sealed class AiProviderConverter : ChoiceConverter
    {
        public AiProviderConverter() : base(("auto", Loc.K("Automatico (consigliato)")), ("copilot", Loc.K("GitHub Copilot (abbonamento)")),
            ("claudecode", Loc.K("Claude Code (abbonamento Claude)")), ("claude", Loc.K("Claude API (API key Anthropic)")), ("openai", Loc.K("OpenAI-compatibile / GitHub Models"))) { }
    }

    internal sealed class ClaudeModelConverter : ChoiceConverter
    {
        public ClaudeModelConverter() : base(("claude-sonnet-5-5", Loc.K("Sonnet (consigliato, equilibrato)")), ("claude-haiku-5-5", Loc.K("Haiku (il più economico e veloce)")),
            ("claude-opus-5-5", Loc.K("Opus (il più potente, costoso)"))) { }
    }

    internal sealed class CopilotModelConverter : ChoiceConverter
    {
        public CopilotModelConverter() : base(("default", Loc.K("Predefinito di Copilot (consigliato)")), ("gpt-4.1", Loc.K("GPT-4.1")),
            ("gpt-5-mini", Loc.K("GPT-5 mini")), ("claude-sonnet-4.5", Loc.K("Claude Sonnet 4.5"))) { }
    }

    internal sealed class AiModelConverter : ChoiceConverter
    {
        public AiModelConverter() : base(("openai/gpt-4o-mini", Loc.K("GPT-4o mini (consigliato, gratuito su GitHub Models)")), ("openai/gpt-4o", Loc.K("GPT-4o")),
            ("openai/gpt-4.1-mini", Loc.K("GPT-4.1 mini")), ("openai/gpt-4.1", Loc.K("GPT-4.1"))) { }
    }

    internal sealed class LanguageConverter : ChoiceConverter
    {
        public LanguageConverter() : base(("auto", Loc.K("Automatica")), ("italiano", Loc.K("Italiano")), ("english", Loc.K("English"))) { }
    }

    internal sealed class UiLanguageConverter : ChoiceConverter
    {
        public UiLanguageConverter() : base(("auto", Loc.K("Automatica (lingua di Visual Studio)")), ("it", Loc.K("Italiano")), ("en", Loc.K("English"))) { }
    }

    internal sealed class WindowThemeConverter : ChoiceConverter
    {
        public WindowThemeConverter() : base(("auto", Loc.K("Automatico (segue Visual Studio)")), ("light", Loc.K("Chiaro")), ("dark", Loc.K("Scuro")), ("blue", Loc.K("Blu"))) { }
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
            { "aiProvider", "claudeModel", "anthropicApiKey", "copilotModel", "aiEndpoint", "aiApiKey", "aiModel", "defaultLanguage", "workingCopyPath", "windowTheme", "uiLanguage" };

        [LCategory("Commit message AI"), LDisplayName("Provider AI per i commit message"), DefaultValue("auto"), TypeConverter(typeof(AiProviderConverter))]
        [LDescription("Automatico prova Copilot, Claude Code, Claude API e OpenAI-compatibile in quest'ordine.")]
        public string AiProvider { get; set; } = "auto";

        [LCategory("Commit message AI"), LDisplayName("Modello Claude (solo Claude Code e Claude API)"), DefaultValue("claude-sonnet-5-5"), TypeConverter(typeof(ClaudeModelConverter))]
        public string ClaudeModel { get; set; } = "claude-sonnet-5-5";

        [LCategory("Commit message AI"), LDisplayName("API key Anthropic (solo Claude API)"), PasswordPropertyText(true)]
        [LDescription("Vuota = variabile d'ambiente ANTHROPIC_API_KEY. Salvata cifrata (DPAPI, solo per il tuo utente Windows) nelle impostazioni di Visual Studio.")]
        public string AnthropicApiKey { get; set; } = "";

        [LCategory("Commit message AI"), LDisplayName("Modello Copilot (solo Copilot)"), DefaultValue("default"), TypeConverter(typeof(CopilotModelConverter))]
        [LDescription("Serve un abbonamento Copilot e 'gh auth login'.")]
        public string CopilotModel { get; set; } = "default";

        [LCategory("Commit message AI"), LDisplayName("URL endpoint (solo OpenAI-compatibile)"), DefaultValue(GitHubModelsEndpoint)]
        [LDescription("Endpoint Chat Completions compatibile OpenAI. Default: GitHub Models.")]
        public string AiEndpoint { get; set; } = GitHubModelsEndpoint;

        [LCategory("Commit message AI"), LDisplayName("API key o GitHub PAT (solo OpenAI-compatibile)"), PasswordPropertyText(true)]
        [LDescription("Salvata cifrata (DPAPI, solo per il tuo utente Windows) nelle impostazioni di Visual Studio.")]
        public string AiApiKey { get; set; } = "";

        [LCategory("Commit message AI"), LDisplayName("Modello (solo OpenAI-compatibile / GitHub Models)"), DefaultValue("openai/gpt-4o-mini"), TypeConverter(typeof(AiModelConverter))]
        public string AiModel { get; set; } = "openai/gpt-4o-mini";

        [LCategory("Commit message AI"), LDisplayName("Lingua dei commit message"), DefaultValue("auto"), TypeConverter(typeof(LanguageConverter))]
        public string DefaultLanguage { get; set; } = "auto";

        [LCategory("Generale"), LDisplayName("Working Copy Path")]
        [LDescription("Se impostato ha la precedenza sull'auto-rilevamento dalla solution aperta.")]
        public string WorkingCopyPath { get; set; } = "";

        [LCategory("Generale"), LDisplayName("Tema delle finestre SVNAssist"), DefaultValue("auto"), TypeConverter(typeof(WindowThemeConverter))]
        [LDescription("Automatico usa i colori del tema di Visual Studio (chiaro, scuro, blu, alto contrasto).")]
        public string WindowTheme { get; set; } = "auto";

        [LCategory("Generale"), LDisplayName("Lingua dell'interfaccia"), DefaultValue("auto"), TypeConverter(typeof(UiLanguageConverter))]
        [LDescription("Automatica usa la lingua di Visual Studio (italiano se è italiano, altrimenti inglese). Si applica subito a finestre e messaggi; la lingua dei commit message AI è un'impostazione separata.")]
        public string UiLanguage { get; set; } = "auto";

        /// <summary>Cultura UI di Visual Studio (valorizzata dal package sul thread UI): serve a risolvere "auto".</summary>
        internal static string VsCulture = "";

        /// <summary>Lingua effettiva dell'interfaccia, "it" o "en".</summary>
        internal string EffectiveLanguage { get { return Loc.Resolve(Pick(UiLanguage, new UiLanguageConverter(), "auto"), VsCulture); } }

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
                ["uiLanguage"] = Pick(UiLanguage, new UiLanguageConverter(), "auto"),
                ["uiLanguageEffective"] = EffectiveLanguage,
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
                case "uiLanguage": UiLanguage = value; break;
                default: return false;
            }
            return true;
        }
    }
}
