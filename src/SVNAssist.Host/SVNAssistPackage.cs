using System;
using System.Collections.Generic;
using System.Linq;
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
    [InstalledProductRegistration("SVNAssist", "SVN integration with AI commit messages", "0.8.0")]
    [Guid(Ids.PackageGuid)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChangesToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer, Orientation = ToolWindowOrientation.Left)]
    [ProvideToolWindow(typeof(LogToolWindow), Style = VsDockStyle.MDI, Transient = true)] // il Log e' un documento nell'area editor, come i diff
    // Opzioni moderne di VS 2026 (Unified Settings, Tools > Options > SVNAssist): niente piu' vecchia pagina con popup.
    // SvnAssistOptions resta il modello in memoria (e il posto cifrato di chiavi API e token), vedi UnifiedOptions.
    [ProvideSettingsManifest(PackageRelativeManifestFile = @"Options\registration.json")]
    [ProvideService(typeof(SvnSccService), IsAsyncQueryable = true)]
    [ProvideSourceControlProvider("SVNAssist", "SVNAssist", Ids.SccProviderGuid, Ids.PackageGuid, Ids.SccServiceGuid)]
    // Serve per i solution events (SolutionChanged al backend); il backend parte solo alla prima vista/comando.
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SVNAssistPackage : AsyncPackage
    {
        internal static SVNAssistPackage Instance { get; private set; }
        internal HostService Host { get; private set; }
        internal SvnSccService Scc { get; private set; }
        private SvnAssistOptions _options;
        private ConflictInfoBars _conflictBars; // InfoBar sui documenti in conflitto
        private IdeActivityTracker _activity;
        private string _cachedSolutionWcRoot;
        private readonly Dictionary<string, bool> _externalWcCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Impostazioni (Tools > Options). Solo thread UI.</summary>
        internal SvnAssistOptions Options => _options ?? (_options = (SvnAssistOptions)GetDialogPage(typeof(SvnAssistOptions)));

        internal bool IsSolutionLoading => _activity?.IsSolutionLoading ?? false;

        internal void OnSolutionLoadComplete()
        {
            if (Scc != null && Scc.Active)
                Host.StartSccAsync().FileAndForget("SVNAssist/scc");
        }

        private void OnSccOptionsChanged() => Scc.Reevaluate(null);
        private void OnAfterSolutionChanged(object sender, EventArgs e) => NotifySolution();
        private void OnAppActivated(object sender, EventArgs e)
        {
            SyncUnified(); // di ritorno da Tools > Options: i valori cambiati li' arrivano qui
            Host.OnIdeActiveAsync(true).FileAndForget("SVNAssist/ideActive");
        }

        /// <summary>Opzioni moderne (Unified Settings); null se non disponibili.</summary>
        internal UnifiedOptions Unified { get; private set; }

        /// <summary>Rilegge Unified Settings; se qualcosa e' cambiato salva e avvisa (lingua, tema, scorciatoie, backend).</summary>
        internal void SyncUnified()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Unified == null || !Unified.ReadInto(Options)) return;
            Options.SaveSettingsToStorage();
            Loc.Lang = Options.EffectiveLanguage;
            SvnAssistOptions.RaiseChanged();
        }
        private void OnAppDeactivated(object sender, EventArgs e) => Host.OnIdeActiveAsync(false).FileAndForget("SVNAssist/ideActive");

        protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            Instance = this;
            _ = Options; // crea la pagina sul thread UI
            SvnAssistOptions.VsCulture = System.Globalization.CultureInfo.CurrentUICulture.Name; // lingua dell'interfaccia di VS (thread UI)
            Unified = await UnifiedOptions.CreateAsync(this);
            if (Unified != null)
            {
                Unified.MigrateOnce(Options);
                if (Unified.ReadInto(Options)) Options.SaveSettingsToStorage();
            }
            Loc.Lang = Options.EffectiveLanguage;
            Host = new HostService(this);
            Scc = new SvnSccService(this);
            AddService(typeof(SvnSccService), (c, t, type) => Task.FromResult<object>(Scc), true);
            SvnAssistOptions.Changed += OnSccOptionsChanged;

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
                RegisterCommands(mcs);

            try { _activity = new IdeActivityTracker(this, await GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2); }
            catch (Exception ex) { Log.Write("activity tracker: " + ex.Message); }

            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += OnAfterSolutionChanged;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += OnAfterSolutionChanged;
            NotifySolution();
            // il controllo delle nuove revisioni lavora solo con VS in primo piano
            if (System.Windows.Application.Current is System.Windows.Application app)
            {
                app.Activated += OnAppActivated;
                app.Deactivated += OnAppDeactivated;
            }
            SvnAssistOptions.Changed += ApplyShortcuts;
            ApplyShortcuts();
            try { _conflictBars = new ConflictInfoBars(this); _conflictBars.Start(); }
            catch (Exception ex) { Log.Write("infobar conflitti non attive: " + ex.Message); }
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
                SetBinding(dte, Ids.CmdNextChange, "SVNAssist.NextChange", Options.NextChangeShortcut);
                SetBinding(dte, Ids.CmdPrevChange, "SVNAssist.PreviousChange", Options.PreviousChangeShortcut);
            }
            catch (Exception ex) { Log.Write("scorciatoie: " + ex.Message); }
        }

        private static void SetBinding(EnvDTE.DTE dte, int cmdId, string commandName, string keys)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                keys = (keys ?? "").Trim();
                EnvDTE.Command cmd;
                try { cmd = dte.Commands.Item(Ids.CmdSet.ToString("B"), cmdId); }
                catch { cmd = dte.Commands.Item(commandName); }

                var current = (cmd.Bindings as object[])?.OfType<string>().ToArray() ?? Array.Empty<string>();
                if (keys.Length == 0)
                {
                    if (current.Length > 0) cmd.Bindings = Array.Empty<object>();
                    return;
                }

                // I default dichiarati in SVNAssist.vsct sono gia' applicati da VS: evita riscritture inutili (e potenziali E_INVALIDARG sullo scope).
                if (ShortcutBinding.MatchesAny(current, keys)) return;

                var scopePrefix = ShortcutBinding.ExtractScopePrefix(current);
                if (current.Length == 0)
                {
                    try
                    {
                        var saveAll = dte.Commands.Item("{5EFC7975-14BC-11CF-9B2B-00AA00573819}", 224);
                        var saveBindings = (saveAll?.Bindings as object[])?.OfType<string>();
                        scopePrefix = ShortcutBinding.ExtractScopePrefix(saveBindings);
                    }
                    catch { /* fallback su Global:: */ }
                }

                var binding = ShortcutBinding.WithScope(scopePrefix, keys);
                try
                {
                    cmd.Bindings = new object[] { binding };
                }
                catch (ArgumentException)
                {
                    var canonical = ShortcutBinding.WithScope(scopePrefix, ShortcutBinding.Canonicalize(keys));
                    if (string.Equals(canonical, binding, StringComparison.OrdinalIgnoreCase)) throw;
                    cmd.Bindings = new object[] { canonical };
                }
            }
            catch (Exception ex) { Log.Write("scorciatoia non valida '" + keys + "' per " + commandName + ": " + ex.Message); }
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
            _cachedSolutionWcRoot = string.IsNullOrEmpty(dir) ? null : SvnSccService.FindWorkingCopyRoot(dir);
            _externalWcCache.Clear();
            Host.OnSolutionChangedAsync(dir).FileAndForget("SVNAssist/solution");
            Scc.Reevaluate(dir ?? "");
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
            Add(Ids.CmdBranchTag, () => Host.SendCommandAsync("changes", "branchTagOpen", new JObject { ["kind"] = "branch" }));
            Add(Ids.CmdCreatePatch, () => Host.SendCommandAsync("changes", "createPatch", null));
            Add(Ids.CmdApplyPatch, () => Host.SendCommandAsync("changes", "applyPatch", null));
            Add(Ids.CmdRevertSel, RevertSelectedAsync);
            Add(Ids.CmdLineLog, ShowLogSelectedLinesAsync);
            Add(Ids.CmdWhoTouched, WhoTouchedLineAsync);
            Add(Ids.CmdNextChange, () => DiffNavigateAsync(true));
            Add(Ids.CmdPrevChange, () => DiffNavigateAsync(false));

            // Solution Explorer > Show Log: visibile solo con un elemento selezionato dentro una working copy.
            var seLog = new OleMenuCommand((s, e) => Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (SelectedPath() is string p) await Host.RunPathCommandAsync("log", new[] { p });
            }), new CommandID(Ids.CmdSet, Ids.CmdSeLog));
            seLog.BeforeQueryStatus += (s, e) =>
            {
                var p = SelectedPathWithKind(out var isFolder);
                ((OleMenuCommand)s).Visible = InWorkingCopy(p, isFolder);
            };
            mcs.AddCommand(seLog);

            // Solution Explorer > SVN Add: solo se TUTTI gli elementi selezionati sono non versionati (stato dalla cache SCC; provider spento = nascosto).
            var seAdd = new OleMenuCommand((s, e) => Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var ps = SelectedPaths();
                if (ps.Count > 0) await Host.RunPathCommandAsync("add", ps.ToArray());
            }), new CommandID(Ids.CmdSet, Ids.CmdSeAdd));
            seAdd.BeforeQueryStatus += (s, e) =>
            {
                var ps = SelectedPaths();
                ((OleMenuCommand)s).Visible = ps.Count > 0 && ps.All(Scc.IsUnversioned);
            };
            mcs.AddCommand(seAdd);

            // Solution Explorer > Check for Modifications: solo un file modificato/sostituito/in conflitto (cache SCC); il diff e' quello di Changes (BASE ⇄ working copy).
            var seDiff = new OleMenuCommand((s, e) => Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var ps = SelectedPaths();
                if (ps.Count == 1) await Host.RunPathCommandAsync("diff", ps.ToArray());
            }), new CommandID(Ids.CmdSet, Ids.CmdSeDiff));
            seDiff.BeforeQueryStatus += (s, e) =>
            {
                var ps = SelectedPathsWithKind();
                ((OleMenuCommand)s).Visible = ps.Count == 1 && !ps[0].IsFolder && Scc.IsModified(ps[0].Path);
            };
            mcs.AddCommand(seDiff);

            // Solution Explorer > SVN Merge...: solo su una cartella dentro una working copy; il merge riguarda solo quella cartella (path = cartella).
            var seMerge = new OleMenuCommand((s, e) => Run(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (SelectedPath() is string p && System.IO.Directory.Exists(p))
                    await Host.SendCommandAsync("changes", "mergeOpen", new JObject { ["path"] = p });
            }), new CommandID(Ids.CmdSet, Ids.CmdSeMerge));
            seMerge.BeforeQueryStatus += (s, e) =>
            {
                var p = SelectedPathWithKind(out var isFolder);
                ((OleMenuCommand)s).Visible = p != null && isFolder && InWorkingCopy(p, isKnownFolder: true);
            };
            mcs.AddCommand(seMerge);

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

        private static bool IsPhysicalFolder(IVsHierarchy hier, uint itemId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return hier.GetProperty(itemId, (int)__VSHPROPID.VSHPROPID_TypeGuid, out var v) == VSConstants.S_OK
                && v is Guid g && g == VSConstants.GUID_ItemType_PhysicalFolder;
        }

        private string SelectedPath() => SelectedPathWithKind(out _);

        /// <summary>Percorso e tipo dell'unico elemento selezionato in Solution Explorer senza I/O su disco.</summary>
        private string SelectedPathWithKind(out bool isFolder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            isFolder = false;
            IntPtr hierPtr = IntPtr.Zero, contPtr = IntPtr.Zero;
            try
            {
                if (!(GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection sel)) return null;
                sel.GetCurrentSelection(out hierPtr, out uint itemId, out _, out contPtr);
                if (hierPtr == IntPtr.Zero || itemId == VSConstants.VSITEMID_SELECTION) return null;
                var hier = (IVsHierarchy)Marshal.GetObjectForIUnknown(hierPtr);
                if (hier.GetCanonicalName(itemId, out var name) != VSConstants.S_OK || string.IsNullOrWhiteSpace(name)) return null;
                isFolder = IsPhysicalFolder(hier, itemId);
                return name;
            }
            catch (Exception ex) { Log.Write("selezione SE: " + ex.Message); return null; }
            finally
            {
                if (hierPtr != IntPtr.Zero) Marshal.Release(hierPtr);
                if (contPtr != IntPtr.Zero) Marshal.Release(contPtr);
            }
        }

        /// <summary>Percorsi di tutti gli elementi selezionati in Solution Explorer (selezione multipla inclusa); vuoto se nessuno.</summary>
        private List<string> SelectedPaths() => SelectedPathsWithKind().Select(x => x.Path).ToList();

        private List<(string Path, bool IsFolder)> SelectedPathsWithKind()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var res = new List<(string, bool)>();
            IntPtr hierPtr = IntPtr.Zero, contPtr = IntPtr.Zero;
            try
            {
                if (!(GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection sel)) return res;
                sel.GetCurrentSelection(out hierPtr, out uint itemId, out var multi, out contPtr);
                if (itemId == VSConstants.VSITEMID_SELECTION && multi != null)
                {
                    if (multi.GetSelectionInfo(out uint n, out _) != VSConstants.S_OK || n == 0) return res;
                    var items = new VSITEMSELECTION[n];
                    if (multi.GetSelectedItems(0, n, items) != VSConstants.S_OK) return res;
                    foreach (var it in items)
                        if (it.pHier != null && it.pHier.GetCanonicalName(it.itemid, out var name) == VSConstants.S_OK && !string.IsNullOrWhiteSpace(name))
                            res.Add((name, IsPhysicalFolder(it.pHier, it.itemid)));
                }
                else if (hierPtr != IntPtr.Zero)
                {
                    var hier = (IVsHierarchy)Marshal.GetObjectForIUnknown(hierPtr);
                    if (hier.GetCanonicalName(itemId, out var name) == VSConstants.S_OK && !string.IsNullOrWhiteSpace(name))
                        res.Add((name, IsPhysicalFolder(hier, itemId)));
                }
            }
            catch (Exception ex) { Log.Write("selezione SE: " + ex.Message); res.Clear(); }
            finally
            {
                if (hierPtr != IntPtr.Zero) Marshal.Release(hierPtr);
                if (contPtr != IntPtr.Zero) Marshal.Release(contPtr);
            }
            return res;
        }

        private static bool IsUnderPrefix(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            var r = root.TrimEnd('\\', '/');
            var p = path.TrimEnd('\\', '/');
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (p.Length == r.Length || p[r.Length] == '\\' || p[r.Length] == '/');
        }

        private bool InWorkingCopy(string path, bool isKnownFolder = false)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (Scc != null && !string.IsNullOrEmpty(Scc.Root) && Scc.IsUnderWorkingCopyRoot(path))
                return true;
            if (IsUnderPrefix(_cachedSolutionWcRoot, path))
                return true;

            var trimmed = path.TrimEnd('\\', '/');
            var dir = isKnownFolder ? trimmed : (System.IO.Path.GetDirectoryName(trimmed) ?? trimmed);
            if (string.IsNullOrEmpty(dir)) return false;
            if (_externalWcCache.TryGetValue(dir, out var inWc)) return inWc;

            inWc = SvnSccService.FindWorkingCopyRoot(dir) != null;
            if (_externalWcCache.Count >= 256) _externalWcCache.Clear();
            _externalWcCache[dir] = inWc;
            return inWc;
        }

        /// <summary>
        /// "SVN: Revert Selected Changes": file e righe selezionate (o la riga del cursore) della vista di testo attiva, che e' anche
        /// il pannello destro del diff nativo. Salva il documento se modificato (non c'e' Ctrl+Z: il backend riscrive il file su disco
        /// e VS lo ricarica) e passa al backend [file, "da-a"].
        /// </summary>
        private async Task RevertSelectedAsync()
        {
            var (file, from, to, lines) = await ActiveSelectionAsync();
            if (string.IsNullOrEmpty(file)) { await MessageAsync(Loc.T("Nessun file aperto nell'editor.")); return; }
            await RevertRangeAsync(file, from, to, lines as IVsPersistDocData);
        }

        /// <summary>
        /// "SVN: Show Log for Selected Lines": apre il Log (lo stesso di sempre) con le sole revisioni che hanno toccato le righe
        /// selezionate o quella del cursore, merge compresi. Salva il documento se modificato: le righe si calcolano dal file su disco.
        /// </summary>
        private async Task ShowLogSelectedLinesAsync()
        {
            var (file, from, to, lines) = await ActiveSelectionAsync();
            if (string.IsNullOrEmpty(file)) { await MessageAsync(Loc.T("Nessun file aperto nell'editor.")); return; }
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (lines is IVsPersistDocData doc && doc.IsDocDataDirty(out int dirty) == VSConstants.S_OK && dirty != 0)
                doc.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out _, out _);
            await Host.RunPathCommandAsync("linelog", new[] { file, from + "-" + to });
        }

        /// <summary>
        /// "SVN: Who Changed This Line": risposta al volo per la riga del cursore (blame del file, in cache nel backend),
        /// senza accendere il blame nella barra dell'editor. Dal messaggio si puo' aprire il Show Log della riga.
        /// </summary>
        private async Task WhoTouchedLineAsync()
        {
            var (file, from, _, _) = await ActiveSelectionAsync();
            if (string.IsNullOrEmpty(file)) { await MessageAsync(Loc.T("Nessun file aperto nell'editor.")); return; }
            var raw = await Host.GetBlameAsync(file);
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var o = Newtonsoft.Json.Linq.JObject.Parse(raw);
            if (o["error"] != null) { await MessageAsync(Loc.T("Blame non disponibile: {0}", (string)o["error"])); return; }
            var lines = o["lines"]?.ToObject<int[]>() ?? new int[0];
            var revs = o["revs"] as Newtonsoft.Json.Linq.JArray;
            var i = from >= 1 && from <= lines.Length ? lines[from - 1] : -1;
            string text;
            if (i < 0 || revs == null || i >= revs.Count)
                text = Loc.T("Riga {0}: modifica locale non committata", from);
            else
            {
                var r = revs[i];
                var rev = (long)r["r"];
                var msg = await Host.GetBlameMessageAsync(file, rev);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                text = Loc.T("Riga {0}: ultima modifica in r{1}", from, rev) + "\n" + (string)r["a"] + " · " + (string)r["d"] + (string.IsNullOrEmpty(msg) ? "" : "\n\n" + msg);
            }
            if (PromptDialog.Show(text, new[] { Loc.T("Show Log di questa riga"), "OK" }) == 0)
                await Host.RunPathCommandAsync("linelog", new[] { file, from + "-" + from });
        }

        /// <summary>File e righe selezionate (1-based; senza selezione la riga del cursore) della vista di testo attiva; file null se non c'e'.</summary>
        private async Task<(string File, int From, int To, IVsTextLines Lines)> ActiveSelectionAsync()
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
                if (from > to) (from, to) = (to, from); // selezione fatta dal basso verso l'alto
            }
            return (file, from, to, lines);
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
            if (!(await GetServiceAsync(typeof(SComponentModel)) is IComponentModel cm)) return;
            var view = cm.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView);
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
            if (disposing)
            {
                SvnAssistOptions.Changed -= OnSccOptionsChanged;
                SvnAssistOptions.Changed -= ApplyShortcuts;
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution -= OnAfterSolutionChanged;
                Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution -= OnAfterSolutionChanged;
                try
                {
                    if (System.Windows.Application.Current is System.Windows.Application app)
                    {
                        app.Activated -= OnAppActivated;
                        app.Deactivated -= OnAppDeactivated;
                    }
                }
                catch { }
                _conflictBars?.Dispose();
                _activity?.Dispose();
                Host?.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class IdeActivityTracker : IVsUpdateSolutionEvents2, IDisposable
        {
            private readonly SVNAssistPackage _pkg;
            private readonly IVsSolutionBuildManager2 _buildMgr;
            private readonly uint _buildCookie;
            private readonly UIContext _solOpening = KnownUIContexts.SolutionOpeningContext;
            private readonly UIContext _solLoaded = KnownUIContexts.SolutionExistsAndFullyLoadedContext;
            private bool _isBuilding;
            private bool _isSolutionLoading;
            private bool _lastPaused;

            public IdeActivityTracker(SVNAssistPackage pkg, IVsSolutionBuildManager2 buildMgr)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _pkg = pkg;
                _buildMgr = buildMgr;
                _isSolutionLoading = _solOpening.IsActive;
                if (_buildMgr != null) _buildMgr.AdviseUpdateSolutionEvents(this, out _buildCookie);
                _solOpening.UIContextChanged += OnSolutionOpeningChanged;
                _solLoaded.UIContextChanged += OnSolutionLoadedChanged;
                UpdatePaused();
            }

            public bool IsSolutionLoading => _isSolutionLoading;

            private void OnSolutionOpeningChanged(object sender, UIContextChangedEventArgs e)
            {
                _isSolutionLoading = e.Activated;
                UpdatePaused();
            }

            private void OnSolutionLoadedChanged(object sender, UIContextChangedEventArgs e)
            {
                if (!e.Activated) return;
                _isSolutionLoading = false;
                UpdatePaused();
                _pkg.OnSolutionLoadComplete();
            }

            private void UpdatePaused()
            {
                var paused = _isBuilding || _isSolutionLoading;
                if (paused == _lastPaused) return;
                _lastPaused = paused;
                _pkg.Host.SetAutoRefreshPausedAsync(paused).FileAndForget("SVNAssist/pause");
            }

            public int UpdateSolution_Begin(ref int pfCancelUpdate) { _isBuilding = true; UpdatePaused(); return VSConstants.S_OK; }
            public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) { _isBuilding = false; UpdatePaused(); return VSConstants.S_OK; }
            public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
            public int UpdateSolution_Cancel() { _isBuilding = false; UpdatePaused(); return VSConstants.S_OK; }
            public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
            public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel) => VSConstants.S_OK;
            public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel) => VSConstants.S_OK;

            public void Dispose()
            {
                _solOpening.UIContextChanged -= OnSolutionOpeningChanged;
                _solLoaded.UIContextChanged -= OnSolutionLoadedChanged;
                try { if (_buildCookie != 0 && _buildMgr != null && ThreadHelper.CheckAccess()) _buildMgr.UnadviseUpdateSolutionEvents(_buildCookie); } catch { }
            }
        }
    }
}
