using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace SVNAssist.Host
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SVNAssist", "SVN integration with AI commit messages", "0.2.0")]
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
            // Verifica end-to-end: apre la finestra Changes a package caricato (spento di default).
            if (Environment.GetEnvironmentVariable("SVNASSIST_AUTO_OPEN") == "1")
                JoinableTaskFactory.RunAsync(async () => { await Task.Delay(5000); await ShowViewAsync("changes"); }).FileAndForget("SVNAssist/autoopen");
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
            Add(Ids.CmdNextChange, () => DiffNavigateAsync("Diff.NextDifference"));
            Add(Ids.CmdPrevChange, () => DiffNavigateAsync("Diff.PreviousDifference"));

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
            if (await GetServiceAsync(typeof(SVsTextManager)) is IVsTextManager tm
                && tm.GetActiveView(1, null, out var view) == VSConstants.S_OK && view != null
                && view.GetBuffer(out var lines) == VSConstants.S_OK)
            {
                if (lines is IPersistFileFormat pff) { pff.GetCurFile(out file, out _); }
                view.GetSelection(out int sl, out _, out int el, out int ec);
                from = sl + 1; to = el + 1;
                if (el > sl && ec == 0) to = el; // selezione che finisce a inizio riga: quella riga non e' inclusa
                if (lines is IVsPersistDocData pdd && pdd.IsDocDataDirty(out int dirty) == VSConstants.S_OK && dirty != 0)
                {
                    if (pdd.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out _, out _) != VSConstants.S_OK)
                    { await MessageAsync(Loc.T("Impossibile salvare il file prima di annullare le righe.")); return; }
                }
            }
            if (string.IsNullOrEmpty(file)) { await MessageAsync(Loc.T("Nessun file aperto nell'editor.")); return; }
            await Host.RunPathCommandAsync("revertlines", new[] { file, from + "-" + to });
        }

        /// <summary>Prossima/precedente differenza nel diff nativo di VS (comandi VS esistenti); fuori da un diff non fa nulla.</summary>
        private async Task DiffNavigateAsync(string vsCommand)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            try { ((EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE))).ExecuteCommand(vsCommand); }
            catch (Exception ex) { Log.Write("diff navigate: " + ex.Message); }
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
