using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using SVNAssist.Navigation;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace SVNAssist.Host
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SVNAssist", "SVN integration with AI commit messages", "0.3.1")]
    [Guid(Ids.PackageGuid)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChangesToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer, Orientation = ToolWindowOrientation.Left)]
    [ProvideToolWindow(typeof(LogToolWindow), Style = VsDockStyle.MDI, Transient = true)] // il Log e' un documento nell'area editor, come i diff
    [ProvideOptionPage(typeof(SvnAssistOptions), "SVNAssist", "General", 0, 0, true)]
    // Serve per i solution events (SolutionChanged al backend); il backend parte solo alla prima vista/comando.
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SVNAssistPackage : AsyncPackage
    {
        internal static SVNAssistPackage Instance { get; private set; }
        internal HostService Host { get; private set; }
        private SvnAssistOptions _options;

        /// <summary>Impostazioni (Tools > Options). Solo thread UI.</summary>
        internal SvnAssistOptions Options => _options ?? (_options = (SvnAssistOptions)GetDialogPage(typeof(SvnAssistOptions)));

        protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            Instance = this;
            _ = Options; // crea la pagina sul thread UI
            SvnAssistOptions.VsCulture = System.Globalization.CultureInfo.CurrentUICulture.Name; // lingua dell'interfaccia di VS (thread UI)
            Loc.Lang = Options.EffectiveLanguage;
            Host = new HostService(this);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
                RegisterCommands(mcs);

            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += (s, e) => NotifySolution();
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += (s, e) => NotifySolution();
            NotifySolution();
            // il controllo delle nuove revisioni lavora solo con VS in primo piano
            if (System.Windows.Application.Current is System.Windows.Application app)
            {
                app.Activated += (s, e) => Host.OnIdeActiveAsync(true).FileAndForget("SVNAssist/ideActive");
                app.Deactivated += (s, e) => Host.OnIdeActiveAsync(false).FileAndForget("SVNAssist/ideActive");
            }
            SvnAssistOptions.Changed += ApplyShortcuts;
            ApplyShortcuts();
            // Verifica end-to-end: apre la finestra Changes a package caricato (spento di default).
            if (Environment.GetEnvironmentVariable("SVNASSIST_AUTO_OPEN") == "1")
                JoinableTaskFactory.RunAsync(async () => { await Task.Delay(5000); await ShowViewAsync("changes"); }).FileAndForget("SVNAssist/autoopen");
        }

        // ---- scorciatoie personalizzabili (Tools > Options > SVNAssist > Scorciatoie) ----

        /// <summary>
        /// Applica le scorciatoie delle Options ai comandi con EnvDTE <c>Command.Bindings</c> ("Global::Alt+F8"); vuoto = nessuna scorciatoia.
        /// Valore non valido: errore nel log e binding precedente invariato, mai un'eccezione.
        /// </summary>
        private void ApplyShortcuts()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) { Log.Write("scorciatoie: DTE non disponibile"); return; }
                SetBinding(dte, "SVNAssist.NextChange", Options.NextChangeShortcut);
                SetBinding(dte, "SVNAssist.PreviousChange", Options.PreviousChangeShortcut);
            }
            catch (Exception ex) { Log.Write("scorciatoie: " + ex.Message); }
        }

        private static void SetBinding(EnvDTE.DTE dte, string command, string keys)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                keys = (keys ?? "").Trim();
                var cmd = dte.Commands.Item(command);
                if (keys.Length == 0) { cmd.Bindings = new object[0]; return; }
                // un solo tasto/accordo; formato DTE "Scope::Keys"
                var binding = "Global::" + keys;
                var current = cmd.Bindings as object[];
                if (current != null && current.Length == 1 && string.Equals(current[0] as string, binding, StringComparison.OrdinalIgnoreCase)) return;
                cmd.Bindings = new object[] { binding };
            }
            catch (Exception ex) { Log.Write("scorciatoia non valida '" + keys + "' per " + command + ": " + ex.Message); }
        }

        // ---- solution ----

        private void NotifySolution()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = null;
            try
            {
                if (GetService(typeof(SVsSolution)) is IVsSolution sol && sol.GetSolutionInfo(out var d, out _, out _) == VSConstants.S_OK && !string.IsNullOrEmpty(d))
                    dir = d.TrimEnd('\\', '/');
            }
            catch (Exception ex) { Log.Write("solution info: " + ex.Message); }
            Host.OnSolutionChangedAsync(dir).FileAndForget("SVNAssist/solution");
        }

        // ---- finestre ----

        internal async Task ShowViewAsync(string view, string title = null)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var type = view == "log" ? typeof(LogToolWindow) : typeof(ChangesToolWindow);
            var pane = await ShowToolWindowAsync(type, 0, true, DisposalToken);
            if (title != null && pane != null) pane.Caption = title; // una sola finestra Log: il titolo segue l'ultimo percorso
            // VS ricorda la posizione: se il Log e' stato agganciato tra i tool window lo riportiamo nell'area documenti, accanto ai diff
            if (view == "log" && pane?.Frame is IVsWindowFrame frame)
            {
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
                frame.Show();
            }
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id) =>
            toolWindowType == typeof(LogToolWindow) ? "Log" : "SVN Changes";

        // Factory asincrona: VS aspetta InitializeAsync prima di creare le finestre (anche quelle ripristinate all'avvio)
        // e passa l'HostService al costruttore della finestra.
        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType) =>
            toolWindowType == typeof(ChangesToolWindow).GUID || toolWindowType == typeof(LogToolWindow).GUID ? this : null;

        protected override Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken ct) => Task.FromResult<object>(Host);

        // ---- comandi (.vsct) ----

        private void RegisterCommands(OleMenuCommandService mcs)
        {
            void Add(int id, Func<Task> run) => mcs.AddCommand(new MenuCommand((s, e) => Run(run), new CommandID(Ids.CmdSet, id)));

            Add(Ids.CmdChanges, () => ShowViewAsync("changes"));
            Add(Ids.CmdCommit, () => ShowViewAsync("changes"));
            Add(Ids.CmdLog, () => ShowViewAsync("log"));
            Add(Ids.CmdUpdate, () => Host.SendCommandAsync("changes", "update", null));
            Add(Ids.CmdAiSetup, () => Host.SendCommandAsync("changes", "wizardOpen", null));
            Add(Ids.CmdUpdateRev, () => Host.SendCommandAsync("changes", "updateToRevision", null));
            Add(Ids.CmdCleanup, () => Host.SendCommandAsync("changes", "cleanup", null));
            Add(Ids.CmdSwitch, () => Host.SendCommandAsync("changes", "switchOpen", null));
            Add(Ids.CmdMerge, () => Host.SendCommandAsync("changes", "mergeOpen", null));
            Add(Ids.CmdRevertSel, RevertSelectedAsync);
            Add(Ids.CmdNextChange, () => DiffNavigateAsync(true));
            Add(Ids.CmdPrevChange, () => DiffNavigateAsync(false));

            // Sottomenu Lingua: tre voci a scelta esclusiva, la spunta segue l'impostazione.
            void AddLang(int id, string value)
            {
                var cmd = new OleMenuCommand((s, e) => Run(() => SetLanguageAsync(value)), new CommandID(Ids.CmdSet, id));
                cmd.BeforeQueryStatus += (s, e) => ((OleMenuCommand)s).Checked = Options.UiLanguage == value;
                mcs.AddCommand(cmd);
            }
            AddLang(Ids.CmdLangAuto, "auto");
            AddLang(Ids.CmdLangIt, "it");
            AddLang(Ids.CmdLangEn, "en");

        }

        /// <summary>
        /// "SVN: Revert Selected Changes": file e righe selezionate (o la riga del cursore) della vista di testo attiva, che e' anche
        /// il pannello destro del diff nativo. Salva il documento se modificato (non c'e' Ctrl+Z: il backend riscrive il file su disco
        /// e VS lo ricarica) e passa al backend [file, "da-a"].
        /// </summary>
        private async Task RevertSelectedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            string file = null; int from = 0, to = 0;
            IVsTextLines lines = null;
            if (await GetServiceAsync(typeof(SVsTextManager)) is IVsTextManager tm
                && tm.GetActiveView(1, null, out var view) == VSConstants.S_OK && view != null
                && view.GetBuffer(out lines) == VSConstants.S_OK)
            {
                if (lines is IPersistFileFormat pff) { pff.GetCurFile(out file, out _); }
                view.GetSelection(out int sl, out _, out int el, out int ec);
                from = sl + 1; to = el + 1;
                if (el > sl && ec == 0) to = el; // selezione che finisce a inizio riga: quella riga non e' inclusa
            }
            if (string.IsNullOrEmpty(file)) { await MessageAsync(Loc.T("Nessun file aperto nell'editor.")); return; }
            await RevertRangeAsync(file, from, to, lines as IVsPersistDocData);
        }

        /// <summary>
        /// Annulla le righe from..to (1-based) del file: salva prima il documento se modificato (non c'e' Ctrl+Z: il backend riscrive
        /// il file su disco e VS lo ricarica). L'esito lo mostra il backend come messaggio di stato in Changes, senza dialoghi.
        /// </summary>
        internal async Task RevertRangeAsync(string file, int from, int to, IVsPersistDocData doc)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (doc != null && doc.IsDocDataDirty(out int dirty) == VSConstants.S_OK && dirty != 0
                && doc.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out _, out _) != VSConstants.S_OK)
            { await MessageAsync(Loc.T("Impossibile salvare il file prima di annullare le righe.")); return; }
            await Host.RunPathCommandAsync("revertlines", new[] { file, from + "-" + to });
        }

        /// <summary>
        /// Prossima/precedente modifica, con giro. In un diff usa il viewer nativo (ScrollToNext/PreviousChange: sposta il cursore nel
        /// pannello attivo); in un editor normale usa i blocchi del svn diff (backend) e la riga del cursore. Nessun nome di comando VS.
        /// </summary>
        private async Task DiffNavigateAsync(bool next)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await GetServiceAsync(typeof(SVsTextManager)) is IVsTextManager tm) || tm.GetActiveView(1, null, out var vsView) != VSConstants.S_OK || vsView == null) return;
            var cm = (IComponentModel)await GetServiceAsync(typeof(SComponentModel));
            var view = cm.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(vsView);
            if (view == null) return;

            bool moved;
            var viewer = cm.GetService<IWpfDifferenceViewerFactoryService>().TryGetViewerForTextView(view);
            if (viewer != null)
                moved = next ? viewer.ScrollToNextChange(true) : viewer.ScrollToPreviousChange(true);
            else
            {
                string file = null;
                if (cm.GetService<ITextDocumentFactoryService>().TryGetTextDocument(view.TextBuffer, out var doc)) file = doc.FilePath;
                if (string.IsNullOrEmpty(file)) return;
                // shortcut: i blocchi vengono dal file su disco; con modifiche non salvate le righe possono slittare, upgrade se serve salvare prima.
                var starts = await Host.GetChangedBlocksAsync(file);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var snap = view.TextSnapshot;
                int cur = snap.GetLineNumberFromPosition(view.Caret.Position.BufferPosition.Position) + 1;
                int line = next ? DiffNav.Next(starts, cur) : DiffNav.Previous(starts, cur);
                if (line < 1 || line > snap.LineCount) return;
                view.Caret.MoveTo(new SnapshotPoint(snap, snap.GetLineFromLineNumber(line - 1).Start));
                moved = true;
            }
            if (moved)
                view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(view.Caret.Position.BufferPosition, 0), EnsureSpanVisibleOptions.AlwaysCenter);
        }

        private async Task SetLanguageAsync(string value)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            Options.UiLanguage = value;
            Options.SaveSettingsToStorage();
            Host.OnLanguageChanged();
        }

        private void Run(Func<Task> action) => JoinableTaskFactory.RunAsync(async () =>
        {
            try { await action(); }
            catch (Exception ex) { Log.Write("comando: " + ex); await MessageAsync(Loc.T("Errore: {0}", ex.Message)); }
        }).FileAndForget("SVNAssist/command");

        private async Task MessageAsync(string text)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(this, text, "SVNAssist", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Host?.Dispose();
            base.Dispose(disposing);
        }
    }
}
