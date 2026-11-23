using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using Task = System.Threading.Tasks.Task;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;

namespace SVNAssist.Host
{
    /// <summary>
    /// Margine sinistro del pannello DESTRO di un diff: un pulsante con l'icona "annulla" (KnownMonikers.Undo) accanto a ogni blocco
    /// modificato, come Git. Clic = annulla quel blocco (stesso percorso di "SVN: Revert Selected Changes": revertlines sul blocco).
    /// Non usa il glyph margin (non e' detto che il diff lo mostri): e' un margine nostro con un Canvas ricalcolato a ogni layout.
    /// shortcut: solo pannello destro del diff, non l'editor normale (servirebbe un tagger sui blocchi del backend); upgrade se richiesto.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(RevertMargin.MarginName)]
    [Order(After = PredefinedMarginNames.Glyph)]
    [MarginContainer(PredefinedMarginNames.Left)]
    // Solo viste diff: evita di istanziare il margine per ogni editor di codice aperto in VS.
    [TextViewRole(DifferenceViewerRoles.RightViewTextViewRole)]
    [TextViewRole(DifferenceViewerRoles.UbiquitousRightViewTextViewRole)]
    [TextViewRole(DifferenceViewerRoles.DiffTextViewRole)]
    [TextViewRole(DifferenceViewerRoles.UbiquitousDiffTextViewRole)]
    internal sealed class RevertMarginProvider : IWpfTextViewMarginProvider
    {
        [Import] internal IWpfDifferenceViewerFactoryService Viewers { get; set; }
        [Import] internal ITextDocumentFactoryService Documents { get; set; }
        [Import] internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin parent) => new RevertMargin(host.TextView, this);
    }

    internal sealed class RevertMargin : Canvas, IWpfTextViewMargin
    {
        internal const string MarginName = "SVNAssist.RevertMargin";
        private const double Size = 18;
        private static readonly bool LogMargin = string.Equals(Environment.GetEnvironmentVariable("SVNASSIST_LOG_MARGIN"), "1", StringComparison.Ordinal);
        private readonly IWpfTextView _view;
        private readonly RevertMarginProvider _p;
        private IDifferenceViewer _viewer;
        private IDifferenceBuffer _buffer;
        private double _lastViewportTop = double.NaN;
        private double _lastViewportHeight = double.NaN;
        private int _lastSnapshotVer = -1;
        private ISnapshotDifference _lastDiff;
        private bool _disposed;

        public RevertMargin(IWpfTextView view, RevertMarginProvider p)
        {
            _view = view; _p = p;
            Width = Size; ClipToBounds = true;
            Background = Brushes.Transparent;
            Visibility = Visibility.Collapsed; // visibile solo se la vista e' il pannello destro di un diff
            // il viewer si registra nel servizio DOPO la creazione delle viste: proviamo solo dopo Loaded/ApplicationIdle senza ascoltare LayoutChanged sui non-diff
            Loaded += (s, e) => TryBindAndRefresh();
            if (SVNAssistPackage.Instance is SVNAssistPackage pkg)
            {
                pkg.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Task.Yield();
                    await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TryBindAndRefresh();
                }).FileAndForget("SVNAssist/RevertMargin/Bind");
            }
        }

        private string _lastLog;
        private void LogOnce(string msg) { if (!LogMargin || msg == _lastLog) return; _lastLog = msg; Log.Write("RevertMargin: " + msg); } // solo con SVNASSIST_LOG_MARGIN=1

        private void TryBindAndRefresh()
        {
            if (_disposed) return;
            if (_viewer == null)
            {
                IDifferenceViewer viewer;
                try { viewer = _p.Viewers.TryGetViewerForTextView(_view); } catch (Exception ex) { LogOnce("TryGetViewerForTextView: " + ex.Message); return; }
                if (viewer == null || viewer.RightView != _view || viewer.DifferenceBuffer == null)
                {
                    if (LogMargin)
                    {
                        var roles = string.Join(",", _view.Roles);
                        LogOnce("nessun pulsante: viewer " + (viewer == null ? "NON trovato" : "trovato ma questa non e' la vista destra (destra=" + (viewer.RightView == _view) + ", buffer=" + (viewer.DifferenceBuffer != null) + ")") + "; ruoli: " + roles);
                    }
                    Visibility = Visibility.Collapsed;
                    return;
                }
                _viewer = viewer;
                _view.LayoutChanged += OnLayout;
                _buffer = viewer.DifferenceBuffer;
                _buffer.SnapshotDifferenceChanged += OnDiffChanged;
            }
            Refresh();
        }

        private void OnLayout(object sender, TextViewLayoutChangedEventArgs e) => Refresh();

        private void OnDiffChanged(object sender, SnapshotDifferenceChangeEventArgs e)
        {
            _lastDiff = null;
            if (SVNAssistPackage.Instance is SVNAssistPackage pkg)
            {
                pkg.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Task.Yield();
                    await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TryBindAndRefresh();
                }).FileAndForget("SVNAssist/RevertMargin/DiffChanged");
            }
        }

        private void Refresh()
        {
            if (_disposed || _viewer == null || _buffer == null) return;
            Visibility = Visibility.Visible;
            var diff = _buffer.CurrentSnapshotDifference;
            if (diff == null) { Children.Clear(); LogOnce("viewer trovato (vista destra), ma CurrentSnapshotDifference nullo"); return; }

            var ver = _view.TextSnapshot.Version.VersionNumber;
            if (ReferenceEquals(diff, _lastDiff)
                && ver == _lastSnapshotVer
                && Math.Abs(_view.ViewportTop - _lastViewportTop) < 0.01
                && Math.Abs(_view.ViewportHeight - _lastViewportHeight) < 0.01)
                return;

            _lastDiff = diff;
            _lastSnapshotVer = ver;
            _lastViewportTop = _view.ViewportTop;
            _lastViewportHeight = _view.ViewportHeight;

            Children.Clear();
            var right = diff.RightBufferSnapshot;
            int nDiffs = 0;
            if (LogMargin) { try { nDiffs = System.Linq.Enumerable.Count(diff.LineDifferences); } catch { } }

            foreach (var d in diff.LineDifferences)
            {
                // Right.Length == 0: cancellazione pura, il pulsante sta sulla riga che segue il punto
                int first = d.Right.Start, last = d.Right.Length > 0 ? d.Right.End - 1 : first;
                if (first >= right.LineCount) first = right.LineCount - 1;
                if (first < 0) continue;
                if (last >= right.LineCount) last = right.LineCount - 1;
                var pt = new SnapshotPoint(right, right.GetLineFromLineNumber(first).Start);
                var mapped = _view.BufferGraph.MapUpToSnapshot(pt, PointTrackingMode.Positive, PositionAffinity.Successor, _view.TextSnapshot);
                if (mapped == null) continue;
                var line = _view.TextViewLines?.GetTextViewLineContainingBufferPosition(mapped.Value);
                if (line == null || line.VisibilityState == VisibilityState.Hidden || line.VisibilityState == VisibilityState.Unattached) continue;

                var icon = new CrispImage { Moniker = KnownMonikers.Undo, Width = 16, Height = 16 };
                var btn = new Button
                {
                    Content = icon, Width = Size, Height = Math.Min(Size, Math.Max(line.Height, 16)), Padding = new Thickness(0),
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = Loc.T("Annulla questa modifica (riporta a BASE)"), Focusable = false,
                };
                int from = first + 1, to = last + 1; // 1-based
                btn.Click += (s, e) => Revert(from, to);
                SetTop(btn, line.Top - _view.ViewportTop);
                Children.Add(btn);
            }
            if (LogMargin)
                LogOnce("viewer trovato, vista destra, differenze " + nDiffs + ", pulsanti creati " + Children.Count + "; ruoli: " + string.Join(",", _view.Roles));
        }

        private void Revert(int from, int to)
        {
            var pkg = SVNAssistPackage.Instance;
            var buf = _buffer?.BaseRightBuffer ?? _buffer?.RightBuffer;
            if (pkg == null || buf == null || !_p.Documents.TryGetTextDocument(buf, out var doc)) return;
            var file = doc.FilePath;
            // conferma come in Git: l'annulla riscrive il file su disco e non c'e' Ctrl+Z
            if (PromptDialog.Show(Loc.T("Annullare questa modifica (righe {0}-{1})? Il blocco torna alla versione BASE.", from, to), new[] { Loc.T("Annulla modifica"), Loc.T("Annulla") }) != 0) return;
            var adapter = _p.Adapters.GetBufferAdapter(buf) as IVsPersistDocData;
            pkg.JoinableTaskFactory.RunAsync(() => pkg.RevertRangeAsync(file, from, to, adapter)).FileAndForget("SVNAssist/revertGlyph");
        }

        // ---- IWpfTextViewMargin ----
        public FrameworkElement VisualElement => this;
        public double MarginSize => Size;
        public bool Enabled => true;
        public ITextViewMargin GetTextViewMargin(string marginName) => marginName == MarginName ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view.LayoutChanged -= OnLayout;
            if (_buffer != null) _buffer.SnapshotDifferenceChanged -= OnDiffChanged;
        }
    }
}
