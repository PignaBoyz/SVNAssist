using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace SVNAssist.Spike
{
    [Guid("0b7a3f4c-2d6e-4f0b-8f19-3c7d9a1e5b22")]
    public sealed class SpikeToolWindow : ToolWindowPane
    {
        private readonly WebViewControl _view = new WebViewControl();

        public SpikeToolWindow() : base(null)
        {
            Caption = "SVNAssist Spike";
            Content = _view;
        }

        // VS intercetta Del, Ctrl+A/C/V, frecce prima della WebView: se il focus e' nella
        // WebView lasciamo la tastiera al browser (ritornare false = "non gestito da VS").
        protected override bool PreProcessMessage(ref Message m)
        {
            if (_view.IsKeyboardFocusWithin)
            {
                _view.Log("PreProcessMessage msg=0x" + m.Msg.ToString("X") + " -> lasciato alla WebView");
                return false;
            }
            return base.PreProcessMessage(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _view.Dispose2();
            base.Dispose(disposing);
        }
    }
}
