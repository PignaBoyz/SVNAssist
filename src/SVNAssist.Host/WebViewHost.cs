using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SVNAssist.Host
{
    /// <summary>
    /// Controllo WPF che ospita una vista della UI web (index.html?view=...) in WebView2.
    /// Riusabile da ogni tool window. Ambiente WebView2 condiviso fra le viste.
    /// </summary>
    internal sealed class WebViewHost : UserControl
    {
        private const string VirtualHost = "svnassist.ui";
        private const string Origin = "https://" + VirtualHost + "/";
        private const string RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";

        private static Task<CoreWebView2Environment> _env;
        private static readonly string DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "WebView2");

        private readonly string _view;
        private readonly HostService _host;
        private readonly WebView2 _web = new WebView2();
        private readonly List<string> _queue = new List<string>(); // messaggi arrivati prima del "ready" della pagina
        private bool _pageReady, _started, _disposed;

        public WebViewHost(string view, HostService host)
        {
            _view = view;
            _host = host;
            Content = _web;
            _web.DefaultBackgroundColor = ThemeBridge.WindowBackground(CurrentTheme());
            _host.Register(view, this);
            Loaded += (s, e) => { if (!_started) { _started = true; _ = InitAsync(); } };
        }

        private string CurrentTheme()
        {
            try { return SVNAssistPackage.Instance?.Options.WindowTheme ?? "auto"; } catch { return "auto"; }
        }

        private static Task<CoreWebView2Environment> GetEnvironment()
        {
            // Una sola cartella dati (non scrivibile accanto a devenv.exe) e un solo ambiente per tutti i pannelli.
            if (_env == null || _env.IsFaulted)
            {
                Directory.CreateDirectory(DataFolder);
                _env = CoreWebView2Environment.CreateAsync(null, DataFolder);
            }
            return _env;
        }

        private async Task InitAsync()
        {
            try
            {
                try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch (WebView2RuntimeNotFoundException) { ShowRuntimeMissing(); return; }

                await _web.EnsureCoreWebView2Async(await GetEnvironment());
                var core = _web.CoreWebView2;
                var ui = Path.Combine(Path.GetDirectoryName(typeof(WebViewHost).Assembly.Location), "ui");
                core.SetVirtualHostNameToFolderMapping(VirtualHost, ui, CoreWebView2HostResourceAccessKind.DenyCors);

                var s = core.Settings;
                s.AreDefaultContextMenusEnabled = false; // la UI ha il suo menu contestuale
                s.AreHostObjectsAllowed = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;
#if DEBUG
                s.AreDevToolsEnabled = true;
                s.AreBrowserAcceleratorKeysEnabled = true; // F12 per i DevTools
#else
                s.AreDevToolsEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
#endif
                core.NavigationStarting += (a, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
                core.FrameNavigationStarting += (a, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
                core.NewWindowRequested += (a, e) => e.Handled = true;
                core.PermissionRequested += (a, e) => e.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += OnWebMessage;
                core.ProcessFailed += (a, e) => { Log.Write("WebView2 ProcessFailed " + e.ProcessFailedKind); _pageReady = false; if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) return; core.Reload(); };

                core.Navigate(Origin + "index.html?view=" + _view);
                Log.Write("WebView2 " + _view + " pronta, runtime " + CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex)
            {
                Log.Write("WebView2 init " + _view + ": " + ex);
                ShowError(Loc.T("Impossibile avviare WebView2: {0}", ex.Message));
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith(Origin, StringComparison.Ordinal)) return; // solo la nostra pagina
            string json;
            try { json = e.WebMessageAsJson; } catch { return; }
            if (json.Contains("\"type\":\"ready\"") || json.Contains("\"type\": \"ready\""))
            {
                // La pagina e' pronta: tema subito (evita il flash), poi arriva lo stato dal backend.
                _pageReady = true;
                RefreshTheme();
                foreach (var m in _queue) _web.CoreWebView2.PostWebMessageAsJson(m);
                _queue.Clear();
            }
            _ = _host.FromViewAsync(_view, json);
        }

        /// <summary>Host -> JS. Da chiamare sul thread UI.</summary>
        public void Post(string json)
        {
            if (_disposed || _web.CoreWebView2 == null) return;
            if (!_pageReady) { if (_queue.Count < 50) _queue.Add(json); return; }
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }

        /// <summary>Rimanda il tema (cambio tema di VS, di windowTheme, alto contrasto).</summary>
        public void RefreshTheme()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshTheme)); return; }
            if (_disposed) return;
            var theme = CurrentTheme();
            _web.DefaultBackgroundColor = ThemeBridge.WindowBackground(theme);
            if (_web.CoreWebView2 != null && _pageReady) _web.CoreWebView2.PostWebMessageAsJson(ThemeBridge.BuildMessage(theme));
        }

        // ---- tastiera e focus ----

        /// <summary>
        /// VS intercetta Del, Ctrl+A/C/V, frecce, Tab prima della WebView: true se il focus e' dentro la WebView
        /// (WPF o HWND figlio), cosi' il pane lascia la tastiera al browser.
        /// </summary>
        public bool HasFocusInside()
        {
            if (_web.IsKeyboardFocusWithin) return true;
            try { var h = _web.Handle; return h != IntPtr.Zero && IsChild(h, GetFocus()); } catch { return false; }
        }

        /// <summary>Alla visualizzazione/attivazione della finestra: il focus alla pagina (la UI lo porta sul primo elemento).</summary>
        public void FocusPage()
        {
            if (_disposed) return;
            try
            {
                _web.Focus();
                if (_pageReady) _web.CoreWebView2?.PostWebMessageAsJson("{\"v\":1,\"type\":\"event\",\"name\":\"focus\"}");
            }
            catch (Exception ex) { Log.Write("FocusPage: " + ex.Message); }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);

        // ---- runtime mancante / errori ----

        private void ShowRuntimeMissing()
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
            tb.Inlines.Add(Loc.T("SVNAssist richiede il runtime Microsoft Edge WebView2, che non è installato su questo computer. "));
            var link = new Hyperlink(new Run(Loc.T("Scarica il runtime Evergreen"))) { NavigateUri = new Uri(RuntimeUrl) };
            link.RequestNavigate += (s, e) => { try { Process.Start(new ProcessStartInfo(RuntimeUrl) { UseShellExecute = true }); } catch { } e.Handled = true; };
            tb.Inlines.Add(link);
            tb.Inlines.Add(Loc.T(", installalo e riapri la finestra."));
            Themed(tb);
            Content = tb;
        }

        private void ShowError(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
            Themed(tb);
            Content = tb;
        }

        private static void Themed(TextBlock tb) =>
            tb.SetResourceReference(TextBlock.ForegroundProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);

        public void DisposeHost()
        {
            _disposed = true;
            _host.Unregister(_view, this);
            try { _web.Dispose(); } catch { }
        }
    }
}
