using System;
using System.Drawing;
using System.Windows;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace SVNAssist.Host
{
    /// <summary>
    /// Colori di Visual Studio -> messaggio {type:"theme",kind,vars} (docs/ui-protocol.md).
    /// Si mandano solo le variabili derivabili da VS (sfondi, testi, bordi, selezione, link, font): i colori
    /// di stato (--st-*, diff, sintassi, chip) restano quelli di tokens.css per il `kind` scelto (due palette WCAG).
    /// </summary>
    internal static class ThemeBridge
    {
        // Sfondo della finestra di tokens.css (--bg2) per i temi fissi: serve a DefaultBackgroundColor senza flash.
        private static readonly Color FixedDark = Color.FromArgb(0x25, 0x25, 0x26), FixedLight = Color.FromArgb(0xF3, 0xF3, 0xF3), FixedBlue = Color.FromArgb(0x2B, 0x3D, 0x5C);

        public static Color WindowBackground(string windowTheme)
        {
            switch (windowTheme)
            {
                case "dark": return FixedDark;
                case "light": return FixedLight;
                case "blue": return FixedBlue;
                default: return VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            }
        }

        /// <summary>kind effettivo (light|dark|blue) per windowTheme auto|light|dark|blue.</summary>
        public static string KindFor(string windowTheme) =>
            windowTheme == "light" || windowTheme == "dark" || windowTheme == "blue" ? windowTheme : Kind(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey));

        /// <summary>windowTheme: auto|light|dark|blue.</summary>
        public static string BuildMessage(string windowTheme)
        {
            var vars = new JObject
            {
                ["--font"] = "'" + System.Windows.SystemFonts.MessageFontFamily.Source + "', 'Segoe UI', sans-serif",
                ["--fs"] = Math.Round(SystemParameters.HighContrast ? System.Windows.SystemFonts.MessageFontSize : Math.Max(12, System.Windows.SystemFonts.MessageFontSize + 1)) + "px",
            };
            string kind;
            if (windowTheme == "light" || windowTheme == "dark" || windowTheme == "blue") kind = windowTheme;
            else kind = AutoVars(vars);
            return new JObject { ["v"] = 1, ["type"] = "theme", ["kind"] = kind, ["vars"] = vars }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string AutoVars(JObject vars)
        {
            Color C(ThemeResourceKey k) => VSColorTheme.GetThemedColor(k);
            var bg = C(EnvironmentColors.ToolWindowBackgroundColorKey);
            var fg = C(EnvironmentColors.ToolWindowTextColorKey);
            var border = C(EnvironmentColors.ToolWindowBorderColorKey);
            var input = C(CommonControlsColors.TextBoxBackgroundColorKey);
            var link = C(EnvironmentColors.ControlLinkTextColorKey);
            var sel = C(TreeViewColors.SelectedItemActiveColorKey);
            var selFg = C(TreeViewColors.SelectedItemActiveTextColorKey);
            var kind = Kind(bg);
            bool hc = SystemParameters.HighContrast;

            // bg2 = sfondo della finestra (il body usa --bg2); bg0/bg1 = campi di testo; bg3/bg4 = elementi in rilievo.
            vars["--bg2"] = Hex(bg);
            vars["--bg1"] = Hex(hc ? bg : input);
            vars["--bg0"] = Hex(hc ? bg : Mix(input, kind == "light" ? Color.White : Color.Black, 0.25));
            vars["--bg3"] = Hex(hc ? bg : Mix(bg, fg, 0.07));
            vars["--bg4"] = Hex(hc ? bg : Mix(bg, fg, 0.13));
            vars["--bd"] = Hex(hc ? fg : border);
            vars["--bd2"] = Hex(hc ? fg : Mix(border, fg, 0.5));
            vars["--fg"] = Hex(fg);
            vars["--fg2"] = Hex(Mix(fg, bg, hc ? 0 : 0.25));
            vars["--fg3"] = Hex(Mix(fg, bg, hc ? 0 : 0.35));
            vars["--acc2"] = Hex(link);
            vars["--focus"] = Hex(hc ? link : Mix(link, fg, 0.2));
            vars["--acc-bg"] = Hex(sel);
            if (hc) { vars["--acc"] = Hex(sel); vars["--acc-fg"] = Hex(selFg); vars["--acc-h"] = Hex(sel); }
            return kind;
        }

        /// <summary>dark | light | blue dalla luminanza e dalla tinta dello sfondo.</summary>
        internal static string Kind(Color bg)
        {
            var lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255;
            if (lum >= 0.5) return "light";
            return bg.B - bg.R >= 0x20 && bg.B >= bg.G ? "blue" : "dark";
        }

        internal static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
