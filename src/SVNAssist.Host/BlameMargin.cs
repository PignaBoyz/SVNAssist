using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace SVNAssist.Host
{
    /// <summary>
    /// "Chi ha toccato la riga": pulsante nella barra in basso dell'editor (accanto allo zoom), solo per file in una working copy SVN.
    /// Spento non costa nulla. Clic = acceso: il backend calcola il blame del file una volta (svn blame, in cache finche' la
    /// revisione del file non cambia) e il pulsante mostra revisione, autore, data e messaggio della riga del cursore.
    /// Altro clic = spento. Tasto destro sul pulsante: Show Log della riga, ricalcola, spegni.
    /// Lo stato e' per editor: acceso su un file non accende gli altri.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(BlameMargin.MarginName)]
    [Order(After = PredefinedMarginNames.ZoomControl)]
    [MarginContainer(PredefinedMarginNames.BottomControl)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BlameMarginProvider : IWpfTextViewMarginProvider
    {
        [Import] internal ITextDocumentFactoryService Documents { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin parent)
        {
            if (!Documents.TryGetTextDocument(host.TextView.TextDataModel.DocumentBuffer, out var doc) || string.IsNullOrEmpty(doc.FilePath))
                return null;
            return InWorkingCopy(doc.FilePath) ? new BlameMargin(host.TextView, doc) : null;
        }

        /// <summary>Una cartella .svn sopra il file (stesso criterio del backend, senza lanciare svn).</summary>
        internal static bool InWorkingCopy(string file)
        {
            try
            {
                for (var d = new FileInfo(file).Directory; d != null; d = d.Parent)
                    if (Directory.Exists(Path.Combine(d.FullName, ".svn")))
                        return true;
            }
            catch (Exception) { }
            return false;
        }
    }

    internal sealed class BlameMargin : Border, IWpfTextViewMargin
    {
        internal const string MarginName = "SVNAssist.BlameMargin";
        private readonly IWpfTextView _view;
        private readonly ITextDocument _doc;
        private readonly Button _btn;
        private readonly TextBlock _text;
        private readonly Dictionary<long, string> _messages = new Dictionary<long, string>();
        private bool _on, _loading, _disposed;
        private JArray _revs;
        private int[] _lines;
        private int _lastLine = -1;

        public BlameMargin(IWpfTextView view, ITextDocument doc)
        {
            _view = view; _doc = doc;
            _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
            _btn = new Button
            {
                Content = _text, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false, VerticalAlignment = VerticalAlignment.Stretch,
            };
            _btn.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _btn.Click += (s, e) => Toggle();
            _btn.ContextMenu = BuildMenu();
            Child = _btn;
            _view.Caret.PositionChanged += OnCaret;
            _doc.FileActionOccurred += OnFileAction;
            Render();
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var log = new MenuItem { Header = Loc.T("Show Log di questa riga") };
            log.Click += (s, e) => ShowLineLog();
            var reload = new MenuItem { Header = Loc.T("Ricalcola") };
            reload.Click += (s, e) => { if (_on) Load(); };
            var off = new MenuItem { Header = Loc.T("Spegni") };
            off.Click += (s, e) => { if (_on) Toggle(); };
            menu.Items.Add(log); menu.Items.Add(reload); menu.Items.Add(off);
            menu.Opened += (s, e) => { reload.IsEnabled = _on && !_loading; off.IsEnabled = _on; };
            return menu;
        }

        private void Toggle()
        {
            _on = !_on;
            if (_on) Load();
            else { _revs = null; _lines = null; _messages.Clear(); _loading = false; }
            Render();
        }

        private void Load()
        {
            var pkg = SVNAssistPackage.Instance;
            if (pkg?.Host == null) return;
            _loading = true; Render();
            var file = _doc.FilePath;
            pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                string raw;
                try { raw = await pkg.Host.GetBlameAsync(file); }
                catch (Exception ex) { raw = new JObject { ["error"] = ex.Message }.ToString(); }
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed || !_on) return;
                _loading = false;
                var o = JObject.Parse(raw);
                if (o["error"] != null)
                {
                    _on = false; _revs = null; _lines = null;
                    _btn.ToolTip = Loc.T("Blame non disponibile: {0}", (string)o["error"]);
                    _text.Text = Loc.T("Blame: non disponibile");
                    UpdateLook();
                    return;
                }
                _revs = (JArray)o["revs"];
                _lines = o["lines"].ToObject<int[]>();
                _lastLine = -1;
                Render();
            }).FileAndForget("SVNAssist/BlameMargin/Load");
        }

        private void OnCaret(object sender, CaretPositionChangedEventArgs e) { if (_on && !_loading) Render(); }

        private void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
        {
            // salvato: le righe del file su disco sono cambiate; il blame della BASE resta in cache nel backend, si rifa' solo la mappatura
            if (_on && (e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0) Load();
        }

        /// <summary>Riga del cursore (0-based) nel documento.</summary>
        private int CaretLine()
        {
            var p = _view.Caret.Position.Point.GetPoint(_doc.TextBuffer, PositionAffinity.Successor);
            return p.HasValue ? p.Value.GetContainingLine().LineNumber : -1;
        }

        private void Render()
        {
            if (_disposed) return;
            if (!_on)
            {
                _text.Text = Loc.T("Blame");
                _btn.ToolTip = Loc.T("Chi ha toccato la riga del cursore: clic per accendere (svn blame del file, calcolato una volta), di nuovo per spegnere. Tasto destro: Show Log della riga.");
            }
            else if (_loading)
            {
                _text.Text = Loc.T("Blame: calcolo...");
                _btn.ToolTip = Loc.T("svn blame in corso: sui file con lunga storia puo' richiedere qualche secondo.");
            }
            else if (_lines != null)
            {
                var line = CaretLine();
                if (line == _lastLine) return;
                _lastLine = line;
                var i = line >= 0 && line < _lines.Length ? _lines[line] : -1;
                if (i < 0 || _revs == null || i >= _revs.Count)
                {
                    _text.Text = Loc.T("Riga {0}: modifica locale non committata", line + 1);
                    _btn.ToolTip = Loc.T("Clic per spegnere il blame. Tasto destro: Show Log della riga.");
                }
                else
                {
                    var r = (JObject)_revs[i];
                    var rev = (long)r["r"];
                    var head = string.Format("r{0} · {1} · {2}", rev, (string)r["a"], (string)r["d"]);
                    _text.Text = _messages.TryGetValue(rev, out var m) && m.Length > 0 ? head + " · " + m : head;
                    _btn.ToolTip = Loc.T("Riga {0}: ultima modifica in r{1}. Clic per spegnere il blame. Tasto destro: Show Log della riga.", line + 1, rev);
                    if (!_messages.ContainsKey(rev)) FetchMessage(rev);
                }
            }
            UpdateLook();
        }

        private void FetchMessage(long rev)
        {
            var pkg = SVNAssistPackage.Instance;
            if (pkg?.Host == null) return;
            _messages[rev] = string.Empty; // una richiesta per revisione
            var file = _doc.FilePath;
            pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                string msg;
                try { msg = await pkg.Host.GetBlameMessageAsync(file, rev); } catch (Exception) { msg = string.Empty; }
                await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed || !_on) return;
                _messages[rev] = msg ?? string.Empty;
                _lastLine = -1;
                Render();
            }).FileAndForget("SVNAssist/BlameMargin/Message");
        }

        private void UpdateLook()
        {
            if (_on)
            {
                _btn.SetResourceReference(BackgroundProperty, EnvironmentColors.SystemHighlightBrushKey);
                _btn.SetResourceReference(ForegroundProperty, EnvironmentColors.SystemHighlightTextBrushKey);
            }
            else
            {
                _btn.Background = Brushes.Transparent;
                _btn.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            }
        }

        private void ShowLineLog()
        {
            var pkg = SVNAssistPackage.Instance;
            var line = CaretLine() + 1;
            if (pkg?.Host == null || line < 1) return;
            var file = _doc.FilePath;
            pkg.JoinableTaskFactory.RunAsync(() => pkg.Host.RunPathCommandAsync("linelog", new[] { file, line + "-" + line })).FileAndForget("SVNAssist/BlameMargin/LineLog");
        }

        // ---- IWpfTextViewMargin ----
        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualWidth;
        public bool Enabled => true;
        public ITextViewMargin GetTextViewMargin(string marginName) => marginName == MarginName ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view.Caret.PositionChanged -= OnCaret;
            _doc.FileActionOccurred -= OnFileAction;
        }
    }
}
