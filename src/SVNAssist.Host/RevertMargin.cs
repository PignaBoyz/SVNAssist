using System;
using System.ComponentModel.Composition;
using System.Windows;
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
    [TextViewRole(DifferenceViewerRoles.RightViewTextViewRole)]
    [TextViewRole(DifferenceViewerRoles.UbiquitousRightViewTextViewRole)]
    // Ruoli larghi di proposito (quelli del diff di VS non sono verificabili senza prova): il filtro vero e' in Refresh (solo RightView di un diff).
    [TextViewRole(DifferenceViewerRoles.DiffTextViewRole)]
    [TextViewRole(DifferenceViewerRoles.UbiquitousDiffTextViewRole)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
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
        private readonly IWpfTextView _view;
        private readonly RevertMarginProvider _p;
        private IDifferenceBuffer _buffer;
        private bool _disposed;

        public RevertMargin(IWpfTextView view, RevertMarginProvider p)
        {
            _view = view; _p = p;
            Width = Size; ClipToBounds = true;
            Background = Brushes.Transparent;
            Visibility = Visibility.Collapsed; // visibile solo se la vista e' il pannello destro di un diff
            _view.LayoutChanged += OnLayout;
            // il viewer si registra nel servizio DOPO la creazione delle viste: si riprova al caricamento e a UI inattiva
            Loaded += (s, e) => Refresh();
            Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private string _lastLog;
        private void LogOnce(string msg) { if (msg == _lastLog) return; _lastLog = msg; Log.Write("RevertMargin: " + msg); } // solo se cambia: niente spam a ogni layout

        private void OnLayout(object sender, TextViewLayoutChangedEventArgs e) => Refresh();

        private void OnDiffChanged(object sender, SnapshotDifferenceChangeEventArgs e) => Dispatcher.BeginInvoke(new Action(Refresh));

        private void Refresh()
        {
            if (_disposed) return;
            Children.Clear();
            IDifferenceViewer viewer;
            try { viewer = _p.Viewers.TryGetViewerForTextView(_view); } catch (Exception ex) { LogOnce("TryGetViewerForTextView: " + ex.Message); return; }
            // solo il pannello destro (in modalita' inline non c'e' una vista "destra" da annullare riga per riga)
            var roles = string.Join(",", _view.Roles);
            if (viewer == null || viewer.RightView != _view || viewer.DifferenceBuffer == null)
            {
                LogOnce("nessun pulsante: viewer " + (viewer == null ? "NON trovato" : "trovato ma questa non e' la vista destra (destra=" + (viewer.RightView == _view) + ", buffer=" + (viewer.DifferenceBuffer != null) + ")") + "; ruoli: " + roles);
                Visibility = Visibility.Collapsed; return;
            }
            Visibility = Visibility.Visible;
            if (_buffer != viewer.DifferenceBuffer)
            {
                if (_buffer != null) _buffer.SnapshotDifferenceChanged -= OnDiffChanged;
                _buffer = viewer.DifferenceBuffer;
                _buffer.SnapshotDifferenceChanged += OnDiffChanged;
            }
            var diff = _buffer.CurrentSnapshotDifference;
            if (diff == null) { LogOnce("viewer trovato (vista destra), ma CurrentSnapshotDifference nullo; ruoli: " + roles); return; }
            var right = diff.RightBufferSnapshot;
            int nDiffs = 0;
            try { nDiffs = System.Linq.Enumerable.Count(diff.LineDifferences); } catch { }

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
            LogOnce("viewer trovato, vista destra, differenze " + nDiffs + ", pulsanti creati " + Children.Count + "; ruoli: " + roles);
        }

        private void Revert(int from, int to)
        {
            var pkg = SVNAssistPackage.Instance;
            var buf = _buffer?.BaseRightBuffer ?? _buffer?.RightBuffer;
            if (pkg == null || buf == null || !_p.Documents.TryGetTextDocument(buf, out var doc)) return;
            var file = doc.FilePath;
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
