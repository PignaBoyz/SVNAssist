using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using SVNAssist.Rpc;

namespace SVNAssist.Host
{
    /// <summary>
    /// Implementa IHost (chiamato dal backend) e fa da relay JS &lt;-> backend. Non interpreta i payload UI:
    /// legge solo "type" per distinguere ready/cmd.
    /// </summary>
    internal sealed class HostService : IHost, IDisposable
    {
        private readonly SVNAssistPackage _pkg;
        private readonly BackendProcess _backend;
        private readonly object _gate = new object();
        private readonly Dictionary<string, WebViewHost> _views = new Dictionary<string, WebViewHost>();
        private readonly Dictionary<string, string> _lastReady = new Dictionary<string, string>();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _readyTcs = new Dictionary<string, TaskCompletionSource<bool>>();
        private string _solutionDir;
        private int _seq = 1_000_000; // id dei cmd generati dall'host (i comandi del menu), lontani da quelli della UI

        public HostService(SVNAssistPackage pkg)
        {
            _pkg = pkg;
            _backend = new BackendProcess(this);
            _backend.Restarted += () => _ = OnRestartedAsync();
            _backend.Failed += msg => _ = BroadcastToastAsync(msg);
            SvnAssistOptions.Changed += OnOptionsChanged;
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
        }

        // ---- registro delle viste ----

        public void Register(string view, WebViewHost host) { lock (_gate) _views[view] = host; }
        public void Unregister(string view, WebViewHost host)
        {
            lock (_gate)
            {
                if (_views.TryGetValue(view, out var h) && h == host) { _views.Remove(view); _lastReady.Remove(view); _readyTcs.Remove(view); }
            }
        }

        private WebViewHost Find(string view) { lock (_gate) return _views.TryGetValue(view, out var h) ? h : null; }

        // ---- JS -> host -> backend ----

        /// <summary>Messaggio ricevuto dalla pagina (gia' verificata l'origine). Inoltrato al backend com'e'.</summary>
        public async Task FromViewAsync(string view, string json)
        {
            string type = null; JToken id = null;
            try { var m = JObject.Parse(json); type = (string)m["type"]; id = m["id"]; }
            catch { return; }
            if (type != "ready" && type != "cmd") return;

            if (type == "ready")
            {
                TaskCompletionSource<bool> tcs;
                lock (_gate) { _lastReady[view] = json; _readyTcs.TryGetValue(view, out tcs); }
                tcs?.TrySetResult(true);
            }

            try
            {
                var b = await _backend.GetAsync().ConfigureAwait(false);
                await b.DispatchAsync(view, json).ConfigureAwait(false);
                if (type == "ready") _ = SendSolutionAsync(b);
            }
            catch (Exception ex)
            {
                Log.Write("dispatch " + view + ": " + ex.Message);
                if (type == "cmd" && id != null)
                    await PushAsync(view, new JObject { ["v"] = 1, ["type"] = "reply", ["id"] = id, ["ok"] = false, ["error"] = Loc.T("Servizio SVNAssist non disponibile: {0}", ex.Message) }.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
                else
                    await PushAsync(view, Event("toast", Loc.T("Servizio SVNAssist non disponibile: {0}", ex.Message))).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Comando generato dall'host (menu): apre la finestra, aspetta il "ready" della pagina e inoltra
        /// {v:1,id,type:"cmd",name,args} al backend come se l'avesse mandato la UI.
        /// </summary>
        public async Task SendCommandAsync(string view, string name, JObject args, string title = null)
        {
            await _pkg.ShowViewAsync(view, title).ConfigureAwait(false);
            TaskCompletionSource<bool> tcs;
            lock (_gate)
            {
                if (_lastReady.ContainsKey(view)) tcs = null;
                else if (!_readyTcs.TryGetValue(view, out tcs)) _readyTcs[view] = tcs = new TaskCompletionSource<bool>();
            }
            if (tcs != null && await Task.WhenAny(tcs.Task, Task.Delay(15000)).ConfigureAwait(false) != tcs.Task)
                Log.Write("timeout in attesa del ready di " + view);
            var msg = new JObject { ["v"] = 1, ["id"] = Interlocked.Increment(ref _seq), ["type"] = "cmd", ["name"] = name, ["args"] = args ?? new JObject() };
            await FromViewAsync(view, msg.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
        }

        public async Task RunPathCommandAsync(string command, string[] paths)
        {
            var b = await _backend.GetAsync().ConfigureAwait(false);
            await b.RunPathCommandAsync(command, paths).ConfigureAwait(false);
        }

        private async Task OnRestartedAsync()
        {
            // il backend nuovo non ha stato: ripresentiamo "ready" di ogni vista aperta e la solution.
            try
            {
                var b = await _backend.GetAsync().ConfigureAwait(false);
                await SendSolutionAsync(b).ConfigureAwait(false);
                KeyValuePair<string, string>[] ready;
                lock (_gate) ready = _lastReady.ToArray();
                foreach (var r in ready) await b.DispatchAsync(r.Key, r.Value).ConfigureAwait(false);
                foreach (var v in Views()) await PushAsync(v, Event("backendRestarted", null)).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Write("dopo restart: " + ex.Message); }
        }

        private string[] Views() { lock (_gate) return _views.Keys.ToArray(); }

        private static string Event(string name, string text)
        {
            var o = new JObject { ["v"] = 1, ["type"] = "event", ["name"] = name };
            if (text != null) o["text"] = text;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private Task BroadcastToastAsync(string text) =>
            Task.WhenAll(Views().Select(v => PushAsync(v, Event("toast", text))));

        // ---- solution / impostazioni / tema ----

        public async Task OnSolutionChangedAsync(string dir)
        {
            _solutionDir = dir;
            // Il backend parte solo quando serve (prima vista o comando): se non gira non lo avvio per questo.
            if (!_backend.IsRunning) return;
            try { await SendSolutionAsync(await _backend.GetAsync().ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("solution: " + ex.Message); }
        }

        private async Task SendSolutionAsync(IBackend b)
        {
            await b.SolutionChangedAsync(_solutionDir).ConfigureAwait(false);
            await SendThemeAsync(b).ConfigureAwait(false);
        }

        private async Task SendThemeAsync(IBackend b)
        {
            string kind = null;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            kind = ThemeBridge.KindFor(_pkg.Options.WindowTheme);
            await b.ThemeChangedAsync(kind).ConfigureAwait(false);
        }

        private void NotifyBackendTheme()
        {
            if (!_backend.IsRunning) return;
            _ = Task.Run(async () => { try { await SendThemeAsync(await _backend.GetAsync().ConfigureAwait(false)).ConfigureAwait(false); } catch (Exception ex) { Log.Write("theme: " + ex.Message); } });
        }

        /// <summary>Lingua dell'interfaccia cambiata (menu Lingua o Tools > Options): aggiorna l'host e fa rileggere il backend (finestre incluse).</summary>
        public void OnLanguageChanged()
        {
            Loc.Lang = _pkg.Options.EffectiveLanguage;
            OnOptionsChanged();
        }

        private void OnOptionsChanged()
        {
            Loc.Lang = _pkg.Options.EffectiveLanguage;
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var v in Views()) Find(v)?.RefreshTheme();
                    NotifyBackendTheme();
                    if (_backend.IsRunning) await (await _backend.GetAsync().ConfigureAwait(false)).SettingsChangedAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Write("settings changed: " + ex.Message); }
            });
        }

        private void OnVsThemeChanged(ThemeChangedEventArgs e) { foreach (var v in Views()) Find(v)?.RefreshTheme(); NotifyBackendTheme(); }

        // ---- IHost (chiamato dal backend, su thread di background) ----

        public async Task PushAsync(string view, string messageJson)
        {
            if (SelfTest.Enabled) SelfTest.OnPush(this, view, messageJson);
            var h = Find(view);
            if (h == null) return; // finestra non aperta: al prossimo "ready" il backend rimanda lo stato
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            h.Post(messageJson);
        }

        public async Task<int> PromptAsync(string message, string[] buttons)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return PromptDialog.Show(message, buttons ?? new[] { "OK" });
        }

        public async Task<string> PromptTextAsync(string message, string label, string defaultValue)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return PromptDialog.ShowText(message ?? "", label ?? "", defaultValue ?? "");
        }

        // Le credenziali non si loggano mai (nemmeno la lunghezza).
        public async Task<string[]> PromptCredentialsAsync(string detail, string defaultUser)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return PromptDialog.ShowCredentials(detail ?? "", defaultUser ?? "");
        }

        public async Task<Dictionary<string, string>> GetSettingsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return _pkg.Options.ToDictionary();
        }

        public async Task SetSettingAsync(string key, string value)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var o = _pkg.Options;
            if (!o.TrySet(key, value ?? "")) { Log.Write("setting sconosciuto: " + key); return; }
            o.SaveSettingsToStorage();
            if (key == "windowTheme") { foreach (var v in Views()) Find(v)?.RefreshTheme(); NotifyBackendTheme(); }
        }

        public async Task OpenSettingsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _pkg.ShowOptionPage(typeof(SvnAssistOptions));
        }

        public Task OpenUrlAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException(Loc.T("Solo URL https."));
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }

        public async Task OpenDiffAsync(string leftPath, string rightPath, string leftLabel, string rightLabel, string caption)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var svc = await _pkg.GetServiceAsync(typeof(SVsDifferenceService)) as IVsDifferenceService;
            if (svc == null) throw new InvalidOperationException(Loc.T("IVsDifferenceService non disponibile."));
            svc.OpenComparisonWindow2(leftPath, rightPath, caption, caption, leftLabel, rightLabel, caption, null,
                (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_DetectBinaryFiles);
        }

        public async Task OpenFileAsync(string path)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            VsShellUtilities.OpenDocument(_pkg, path);
        }

        private static string LogTitle(string path)
        {
            var leaf = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path.TrimEnd('\\', '/'));
            return Loc.T("Log: {0}", string.IsNullOrEmpty(leaf) ? path : leaf);
        }

        public async Task ShowLogAsync(string path)
        {
            // Apre il documento Log e fa caricare il percorso (logLoad) come se l'avesse chiesto la UI.
            await SendCommandAsync("log", "logLoad", new JObject { ["path"] = path }, LogTitle(path)).ConfigureAwait(false);
        }

        public Task ShowLogWindowAsync(string title) => _pkg.ShowViewAsync("log", title);

        public Task ShowChangesWindowAsync() => _pkg.ShowViewAsync("changes");

        public async Task ShowFolderDiffAsync(string path)
        {
            await SendCommandAsync("log", "logFolder", new JObject { ["path"] = path }, LogTitle(path)).ConfigureAwait(false);
        }

        public Task RunInTerminalAsync(string command, string workingDir)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/k " + command) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir;
            Process.Start(psi);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            SvnAssistOptions.Changed -= OnOptionsChanged;
            VSColorTheme.ThemeChanged -= OnVsThemeChanged;
            _backend.Dispose();
        }
    }
}
