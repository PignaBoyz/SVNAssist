using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace SVNAssist.Spike
{
    internal sealed class WebViewControl : UserControl
    {
        private const string Host = "svnassist.ui";
        private readonly WebView2 _web = new WebView2();
        private readonly BackendClient _backend = new BackendClient();
        private static readonly string Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "WebView2");

        public WebViewControl()
        {
            Content = _web;
            _web.DefaultBackgroundColor = ToWpf(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey));
            VSColorTheme.ThemeChanged += OnThemeChanged;
            Loaded += (s, e) => _ = InitAsync();
        }

        private bool _started;
        private async System.Threading.Tasks.Task InitAsync()
        {
            if (_started) return;
            _started = true;
            try
            {
                Directory.CreateDirectory(Data);
                var env = await CoreWebView2Environment.CreateAsync(null, Data);
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;
                var ui = Path.Combine(Path.GetDirectoryName(typeof(WebViewControl).Assembly.Location), "ui");
                core.SetVirtualHostNameToFolderMapping(Host, ui, CoreWebView2HostResourceAccessKind.DenyCors);
                var s = core.Settings;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                core.NavigationStarting += (a, e) => { if (!e.Uri.StartsWith("https://" + Host + "/")) e.Cancel = true; };
                core.NewWindowRequested += (a, e) => e.Handled = true;
                core.WebMessageReceived += OnMessage;
                core.NavigationCompleted += (a, e) => PostTheme();
                var auto = Environment.GetEnvironmentVariable("SVNASSIST_SPIKE_AUTO") == "1" ? "?auto=1" : "";
                core.Navigate("https://" + Host + "/index.html" + auto);
                Log("init ok, WebView2 runtime " + CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex) { Log("INIT ERRORE " + ex); }
        }

        private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith("https://" + Host + "/")) return; // controllo origine
            var m = JObject.Parse(e.WebMessageAsJson);
            switch ((string)m["type"])
            {
                case "echo":
                    object reply;
                    try { reply = new { type = "reply", id = (int)m["id"], ok = true, result = await _backend.EchoAsync((string)m["text"]) }; }
                    catch (Exception ex) { reply = new { type = "reply", id = (int)m["id"], ok = false, result = ex.Message }; }
                    _web.CoreWebView2.PostWebMessageAsJson(Newtonsoft.Json.JsonConvert.SerializeObject(reply));
                    break;
                case "done": Log("ROUNDTRIP OK: " + (string)m["result"]); break;
            }
        }

        private void OnThemeChanged(ThemeChangedEventArgs e) => PostTheme();

        private void PostTheme()
        {
            if (_web.CoreWebView2 == null) return;
            var keys = new Dictionary<string, ThemeResourceKey>
            {
                ["--bg"] = EnvironmentColors.ToolWindowBackgroundColorKey,
                ["--fg"] = EnvironmentColors.ToolWindowTextColorKey,
                ["--bd"] = EnvironmentColors.ToolWindowBorderColorKey,
                ["--btn"] = CommonControlsColors.ButtonColorKey,
                ["--btnfg"] = CommonControlsColors.ButtonTextColorKey,
                ["--sel"] = TreeViewColors.SelectedItemActiveColorKey,
                ["--selfg"] = TreeViewColors.SelectedItemActiveTextColorKey,
                ["--link"] = EnvironmentColors.ControlLinkTextColorKey,
            };
            var vars = new JObject();
            foreach (var kv in keys)
            {
                var c = VSColorTheme.GetThemedColor(kv.Value);
                vars[kv.Key] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            vars["--font"] = "'" + System.Windows.SystemFonts.MessageFontFamily.Source + "'";
            vars["--fs"] = System.Windows.SystemFonts.MessageFontSize + "px";
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255;
            var kind = lum < 0.5 ? "dark" : "light";
            _web.DefaultBackgroundColor = ToWpf(bg);
            _web.CoreWebView2.PostWebMessageAsJson(new JObject { ["type"] = "theme", ["kind"] = kind, ["vars"] = vars }.ToString(Newtonsoft.Json.Formatting.None));
            Log("theme -> " + kind + " bg=" + vars["--bg"]);
        }

        private static System.Drawing.Color ToWpf(System.Drawing.Color c) => c;

        internal void Log(string msg)
        {
            Debug.WriteLine("[spike] " + msg);
            SpikePackage.Log(msg);
        }

        internal void Dispose2()
        {
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            _web.Dispose();
            _backend.Dispose();
        }
    }
}
