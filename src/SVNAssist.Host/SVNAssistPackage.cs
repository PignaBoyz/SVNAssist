using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace SVNAssist.Host
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SVNAssist", "Integrazione SVN con commit message AI", "0.2.0")]
    [Guid(Ids.PackageGuid)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChangesToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer, Orientation = ToolWindowOrientation.Left)]
    [ProvideToolWindow(typeof(LogToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer, Orientation = ToolWindowOrientation.Left)]
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
            Host = new HostService(this);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
                RegisterCommands(mcs);

            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += (s, e) => NotifySolution();
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += (s, e) => NotifySolution();
            NotifySolution();
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

        internal async Task ShowViewAsync(string view)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var type = view == "log" ? typeof(LogToolWindow) : typeof(ChangesToolWindow);
            await ShowToolWindowAsync(type, 0, true, DisposalToken);
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id) =>
            toolWindowType == typeof(LogToolWindow) ? "SVN Log" : "SVN Changes";

        protected override Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken ct) => Task.FromResult<object>(null);

        // ---- comandi (.vsct) ----

        private void RegisterCommands(OleMenuCommandService mcs)
        {
            void Add(int id, Func<Task> run) => mcs.AddCommand(new MenuCommand((s, e) => Run(run), new CommandID(Ids.CmdSet, id)));

            Add(Ids.CmdChanges, () => ShowViewAsync("changes"));
            Add(Ids.CmdCommit, () => ShowViewAsync("changes"));
            Add(Ids.CmdLog, () => ShowViewAsync("log"));
            Add(Ids.CmdUpdate, () => Host.SendCommandAsync("changes", "update", null));

            // Solution Explorer: i percorsi selezionati sono un dato dell'host (non vengono dalla pagina) e vanno
            // al backend con IBackend.RunPathCommandAsync(command, paths). Vedi docs/host.md.
            Add(Ids.CmdSeUpdate, () => SeAsync("update", null));
            Add(Ids.CmdSeDiff, () => SeAsync("diff", null));
            Add(Ids.CmdSeAdd, () => SeAsync("add", null));
            Add(Ids.CmdSeRevert, () => SeAsync("revert", null));
            Add(Ids.CmdSeIgnore, () => SeAsync("ignore", null));
            Add(Ids.CmdSeCommit, () => SeAsync("commit", "changes"));
            Add(Ids.CmdSeLog, () => SeAsync("log", "log"));
        }

        private async Task SeAsync(string command, string showView)
        {
            var paths = await SelectedPathsAsync();
            if (paths.Length == 0) { await MessageAsync("Nessun elemento selezionato in Solution Explorer."); return; }
            if (showView != null) await ShowViewAsync(showView);
            await Host.RunPathCommandAsync(command, paths);
        }
        private void Run(Func<Task> action) => JoinableTaskFactory.RunAsync(async () =>
        {
            try { await action(); }
            catch (Exception ex) { Log.Write("comando: " + ex); await MessageAsync("Errore: " + ex.Message); }
        }).FileAndForget("SVNAssist/command");

        private async Task MessageAsync(string text)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(this, text, "SVNAssist", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        /// <summary>Percorsi degli elementi selezionati in Solution Explorer (file, cartelle, progetti, solution).</summary>
        private async Task<string[]> SelectedPathsAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var result = new System.Collections.Generic.List<string>();
            var sel = (IVsMonitorSelection)await GetServiceAsync(typeof(SVsShellMonitorSelection));
            IntPtr hierPtr = IntPtr.Zero, contPtr = IntPtr.Zero;
            try
            {
                sel.GetCurrentSelection(out hierPtr, out uint itemId, out var multi, out contPtr);
                IVsHierarchy hier = hierPtr != IntPtr.Zero ? (IVsHierarchy)Marshal.GetObjectForIUnknown(hierPtr) : null;
                var items = new System.Collections.Generic.List<(IVsHierarchy, uint)>();
                if (itemId == VSConstants.VSITEMID_SELECTION && multi != null)
                {
                    multi.GetSelectionInfo(out uint count, out _);
                    var arr = new VSITEMSELECTION[count];
                    if (count > 0) multi.GetSelectedItems(0, count, arr);
                    foreach (var x in arr) items.Add((x.pHier, x.itemid));
                }
                else if (hier != null) items.Add((hier, itemId));
                foreach (var (h, id) in items)
                    if (h != null && h.GetCanonicalName(id, out var name) == VSConstants.S_OK && !string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            finally
            {
                if (hierPtr != IntPtr.Zero) Marshal.Release(hierPtr);
                if (contPtr != IntPtr.Zero) Marshal.Release(contPtr);
            }
            return result.ToArray();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Host?.Dispose();
            base.Dispose(disposing);
        }
    }
}
