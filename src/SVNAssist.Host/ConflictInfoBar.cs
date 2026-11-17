using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace SVNAssist.Host
{
    /// <summary>
    /// Barra informativa (InfoBar) in cima a un documento aperto in VS quando il file e' in conflitto (stato dalla cache SCC):
    /// "Segna come risolto", "Usa la mia", "Usa la loro", "Usa entrambe", "Apri editor di merge". Ogni pulsante manda al backend
    /// <c>conflictOpPath</c> (lo stesso codice del pannello Conflitti di Changes, con le sue conferme), senza aprire la finestra Changes.
    /// La barra compare quando la scheda del documento viene mostrata (apertura o cambio scheda) e sparisce quando il file non e' piu' in conflitto.
    /// Solo thread UI.
    /// </summary>
    internal sealed class ConflictInfoBars : IVsRunningDocTableEvents, IVsInfoBarUIEvents
    {
        private readonly SVNAssistPackage _pkg;
        private IVsRunningDocumentTable _rdt;
        private uint _cookie;
        private readonly Dictionary<uint, Bar> _bars = new Dictionary<uint, Bar>();

        private sealed class Bar
        {
            public IVsInfoBarUIElement Ui;
            public IVsInfoBarHost Host;
            public string Path;
            public uint UiCookie;
        }

        internal ConflictInfoBars(SVNAssistPackage pkg) { _pkg = pkg; }

        internal void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _rdt = _pkg.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            _rdt?.AdviseRunningDocTableEvents(this, out _cookie);
        }

        // ---- documenti ----

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (pFrame == null) return VSConstants.S_OK;
                if (pFrame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var doc) != VSConstants.S_OK || !(doc is string path) || path.Length == 0)
                    return VSConstants.S_OK;
                var conflicted = _pkg.Scc != null && _pkg.Scc.IsConflicted(path) && File.Exists(path);
                if (_bars.TryGetValue(docCookie, out var existing))
                {
                    if (!conflicted) Remove(docCookie);
                    return VSConstants.S_OK;
                }
                if (conflicted) Add(docCookie, pFrame, path);
            }
            catch (Exception ex) { Log.Write("infobar conflitto: " + ex.Message); }
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            if (dwReadLocksRemaining == 0 && dwEditLocksRemaining == 0) _bars.Remove(docCookie);
            return VSConstants.S_OK;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterSave(uint docCookie) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

        // ---- barra ----

        private void Add(uint docCookie, IVsWindowFrame frame, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(_pkg.GetService(typeof(SVsInfoBarUIFactory)) is IVsInfoBarUIFactory factory)) return;
            if (frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var h) != VSConstants.S_OK || !(h is IVsInfoBarHost host)) return;

            var model = new InfoBarModel(
                new IVsInfoBarTextSpan[] { new InfoBarTextSpan(Loc.T("File in conflitto: scegli come risolverlo.")) },
                new IVsInfoBarActionItem[]
                {
                    new InfoBarButton(Loc.T("Segna come risolto")),
                    new InfoBarButton(Loc.T("Usa la mia")),
                    new InfoBarButton(Loc.T("Usa la loro")),
                    new InfoBarButton(Loc.T("Usa entrambe (mia poi loro)")),
                    new InfoBarButton(Loc.T("Usa entrambe (loro poi mia)")),
                    new InfoBarButton(Loc.T("Apri editor di merge")),
                },
                KnownMonikers.StatusWarning,
                isCloseButtonVisible: true);
            var ui = factory.CreateInfoBar(model);
            if (ui == null) return;
            ui.Advise(this, out var uiCookie);
            host.AddInfoBar(ui);
            _bars[docCookie] = new Bar { Ui = ui, Host = host, Path = path, UiCookie = uiCookie };
        }

        private void Remove(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_bars.TryGetValue(docCookie, out var b)) return;
            _bars.Remove(docCookie);
            try { b.Host.RemoveInfoBar(b.Ui); b.Ui.Unadvise(b.UiCookie); } catch (Exception ex) { Log.Write("infobar conflitto (chiusura): " + ex.Message); }
        }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            var kv = _bars.FirstOrDefault(p => ReferenceEquals(p.Value.Ui, infoBarUIElement));
            if (kv.Value != null) _bars.Remove(kv.Key);
        }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var bar = _bars.Values.FirstOrDefault(b => ReferenceEquals(b.Ui, infoBarUIElement));
            if (bar == null || actionItem == null) return;
            var op = OpOf(actionItem.Text);
            if (op == null) return;
            var path = bar.Path;
            _pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                await _pkg.Host.SendQuietAsync("changes", "conflictOpPath", new JObject { ["op"] = op, ["path"] = path }).ConfigureAwait(false);
                // lo stato SCC si aggiorna dopo qualche istante: poi la barra sparisce da sola se il file non e' piu' in conflitto
                await System.Threading.Tasks.Task.Delay(1500).ConfigureAwait(false);
                await _pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                foreach (var k in _bars.Where(p => !(_pkg.Scc != null && _pkg.Scc.IsConflicted(p.Value.Path))).Select(p => p.Key).ToList()) Remove(k);
            }).FileAndForget("SVNAssist/conflictInfoBar");
        }

        private static string OpOf(string text)
        {
            if (text == Loc.T("Segna come risolto")) return "resolved";
            if (text == Loc.T("Usa la mia")) return "mine";
            if (text == Loc.T("Usa la loro")) return "theirs";
            if (text == Loc.T("Usa entrambe (mia poi loro)")) return "bothMT";
            if (text == Loc.T("Usa entrambe (loro poi mia)")) return "bothTM";
            if (text == Loc.T("Apri editor di merge")) return "edit";
            return null;
        }
    }
}
