using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SVNAssist.Host
{
    /// <summary>Base delle tool window: una WebViewHost con una vista della UI web, tastiera e focus alla WebView.</summary>
    public abstract class WebToolWindow : ToolWindowPane, IVsWindowFrameNotify
    {
        private WebViewHost _view;
        private readonly string _viewName;
        private uint _frameCookie;

        protected WebToolWindow(string view, string caption, object host) : base(null)
        {
            _viewName = view;
            Caption = caption;
            // Mai eccezioni qui: VS ricrea la finestra all'avvio e un errore diventa "Failed to find the tool window".
            try
            {
                var h = host as HostService ?? SVNAssistPackage.Instance?.Host ?? throw new InvalidOperationException(Loc.T("Package SVNAssist non inizializzato."));
                _view = new WebViewHost(_viewName, h);
                Content = _view; // va impostato nel costruttore: dopo, l'Init del frame fallisce con E_UNEXPECTED
            }
            catch (Exception ex)
            {
                Log.Write("tool window " + view + ": " + ex);
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = Loc.T("SVNAssist non è riuscito ad aprire questa finestra: chiudila e riaprila da Extensions > SVNAssist. Dettagli in %LOCALAPPDATA%\\SVNAssist\\logs") + "\n\n" + ex.Message,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new System.Windows.Thickness(12),
                };
            }
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            // Per ricevere OnShow (tab attivata/nascosta) e dare il focus o sospendere la WebView.
            if (Frame is IVsWindowFrame2 f2) f2.Advise(this, out _frameCookie);
        }

        // VS intercetta Del, Ctrl+A/C/V, frecce, Tab prima della WebView: se il focus e' nella WebView
        // ritornare false = "non gestito da VS", il messaggio va al browser.
        protected override bool PreProcessMessage(ref Message m)
        {
            if (_view != null && _view.HasFocusInside()) return false;
            return base.PreProcessMessage(ref m);
        }

        int IVsWindowFrameNotify.OnShow(int fShow)
        {
            var s = (__FRAMESHOW)fShow;
            if (s == __FRAMESHOW.FRAMESHOW_Hidden
                || s == __FRAMESHOW.FRAMESHOW_WinHidden
                || s == __FRAMESHOW.FRAMESHOW_TabDeactivated
                || s == __FRAMESHOW.FRAMESHOW_WinMinimized
                || s == __FRAMESHOW.FRAMESHOW_WinClosed)
            {
                _view?.SetHostVisible(false);
            }
            else if (s == __FRAMESHOW.FRAMESHOW_WinShown || s == __FRAMESHOW.FRAMESHOW_TabActivated)
            {
                _view?.SetHostVisible(true);
                if (_view != null && SVNAssistPackage.Instance is SVNAssistPackage pkg)
                {
                    var v = _view;
                    pkg.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await Task.Yield();
                        await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                        v.FocusPage();
                    }).FileAndForget("SVNAssist/ToolWindow/FocusPage");
                }
            }
            else if (s == __FRAMESHOW.FRAMESHOW_WinRestored || s == __FRAMESHOW.FRAMESHOW_WinMaximized)
            {
                _view?.SetHostVisible(true);
            }
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify.OnDockableChange(int fDockable) => VSConstants.S_OK;
        int IVsWindowFrameNotify.OnMove() => VSConstants.S_OK;
        int IVsWindowFrameNotify.OnSize() => VSConstants.S_OK;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_frameCookie != 0 && Frame is IVsWindowFrame2 f2) f2.Unadvise(_frameCookie); } catch { }
                _frameCookie = 0;
                _view?.DisposeHost();
            }
            base.Dispose(disposing);
        }
    }

    [Guid(Ids.ChangesWindowGuid)]
    public sealed class ChangesToolWindow : WebToolWindow
    {
        public ChangesToolWindow(object host) : base("changes", "SVN Changes", host) { }
    }

    [Guid(Ids.LogWindowGuid)]
    public sealed class LogToolWindow : WebToolWindow
    {
        public LogToolWindow(object host) : base("log", "Log", host) { }
    }
}
