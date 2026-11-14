using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SVNAssist.Scc;

namespace SVNAssist.Host
{
    /// <summary>
    /// SVNAssist come provider di source control di VS, solo per i glifi di stato in Solution Explorer (niente comandi).
    /// Si attiva se la solution e' dentro una working copy SVN e l'opzione e' accesa. Lo stato arriva dal backend
    /// (stessa cache della finestra Changes): GetSccGlyph legge solo la cache in memoria, mai svn sul thread UI.
    /// </summary>
    [Guid(Ids.SccServiceGuid), ComVisible(true)]
    public sealed class SvnSccService : IVsSccProvider, IVsSccManager2
    {
        private readonly SVNAssistPackage _pkg;
        private volatile Dictionary<string, string> _states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _dir, _root;
        private bool _active;

        internal SvnSccService(SVNAssistPackage pkg) { _pkg = pkg; }

        // ---- attivazione (thread UI) ----

        /// <summary>Radice della working copy (cartella con .svn) che contiene dir, null se non c'e'.</summary>
        internal static string FindWorkingCopyRoot(string dir)
        {
            try { for (var d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) if (Directory.Exists(Path.Combine(d, ".svn"))) return d; }
            catch (Exception ex) { Log.Write("scc root: " + ex.Message); }
            return null;
        }

        /// <summary>Solution aperta/chiusa o opzione cambiata: attiva o disattiva il provider.</summary>
        internal void Reevaluate(string solutionDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (solutionDir != null) _dir = solutionDir;
            _root = _pkg.Options.ShowSccGlyphs ? FindWorkingCopyRoot(_dir) : null;
            try
            {
                var reg = ((IServiceProvider)_pkg).GetService(typeof(IVsRegisterScciProvider)) as IVsRegisterScciProvider;
                if (reg == null) return;
                if (_root != null && !_active) reg.RegisterSourceControlProvider(new Guid(Ids.SccProviderGuid));
                else if (_root == null && _active) reg.RegisterSourceControlProvider(Guid.Empty); // GUID nullo = nessun provider
            }
            catch (Exception ex) { Log.Write("scc register: " + ex.Message); }
        }

        public int SetActive()
        {
            _active = true;
            Log.Write("scc: provider attivo, radice " + _root);
            _pkg.Host.StartSccAsync().FileAndForget("SVNAssist/scc");
            return VSConstants.S_OK;
        }

        public int SetInactive() { _active = false; Log.Write("scc: provider disattivato"); return VSConstants.S_OK; }

        public int AnyItemsUnderSourceControl(out int pfResult) { pfResult = _active && _root != null ? 1 : 0; return VSConstants.S_OK; }

        // ---- stato (cache) ----

        /// <summary>Nuovo stato dal backend (qualsiasi thread): aggiorna la cache e ridisegna i glifi di tutti i progetti.</summary>
        internal void SetStates(Dictionary<string, string> states)
        {
            _states = new Dictionary<string, string>(states, StringComparer.OrdinalIgnoreCase);
            _pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                await _pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                // I progetti SDK-style non si registrano da soli dopo l'attivazione: si scorrono quelli caricati.
                var sol = ((IServiceProvider)_pkg).GetService(typeof(SVsSolution)) as IVsSolution;
                var none = Guid.Empty;
                if (sol == null || sol.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref none, out var en) != VSConstants.S_OK) return;
                var h = new IVsHierarchy[1];
                while (en.Next(1, h, out var n) == VSConstants.S_OK && n == 1)
                {
                    try { (h[0] as IVsSccProject2)?.SccGlyphChanged(0, null, null, null); } catch (Exception ex) { Log.Write("scc glyph: " + ex.Message); }
                }
            }).FileAndForget("SVNAssist/sccglyphs");
        }

        internal bool Active => _active;

        private SccGlyph GlyphOf(string path)
        {
            var r = _root;
            if (r == null || string.IsNullOrEmpty(path)) return SccGlyph.None;
            path = path.TrimEnd('\\', '/');
            if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return SccGlyph.None;
            return _states.TryGetValue(path, out var s) ? SccGlyphMap.For(s) : SccGlyph.Normal; // assente = nessuna modifica
        }

        // ---- IVsSccManager2 ----

        public int RegisterSccProject(IVsSccProject2 pscp2Project, string pszSccProjectName, string pszSccAuxPath, string pszSccLocalPath, string pszProvider)
        {
            return VSConstants.S_OK;
        }

        public int UnregisterSccProject(IVsSccProject2 pscp2Project) => VSConstants.S_OK;

        public int IsInstalled(out int pbInstalled) { pbInstalled = 1; return VSConstants.S_OK; }

        public int GetSccGlyph(int cFiles, string[] rgpszFullPaths, VsStateIcon[] rgsiGlyphs, uint[] rgdwSccStatus)
        {
            for (int i = 0; i < cFiles; i++)
            {
                var g = GlyphOf(rgpszFullPaths[i]);
                rgsiGlyphs[i] = Icon(g);
                if (rgdwSccStatus != null) rgdwSccStatus[i] = g == SccGlyph.None ? 0u : 1u; // SCC_STATUS_CONTROLLED
            }
            return VSConstants.S_OK;
        }

        // Icone standard di VS: IVsSccGlyphs (icone custom come "?" di Git) non e' stato richiesto dalla shell in prova, vedi docs/host.md.
        private static VsStateIcon Icon(SccGlyph g)
        {
            switch (g)
            {
                case SccGlyph.Normal: return VsStateIcon.STATEICON_CHECKEDIN;
                case SccGlyph.Modified: return VsStateIcon.STATEICON_CHECKEDOUT;
                case SccGlyph.Added: return VsStateIcon.STATEICON_EDITABLE;
                case SccGlyph.Unversioned: return VsStateIcon.STATEICON_ORPHANED;
                case SccGlyph.Conflict: return VsStateIcon.STATEICON_DISABLED;
                case SccGlyph.Deleted: return VsStateIcon.STATEICON_READONLY;
                default: return VsStateIcon.STATEICON_NOSTATEICON;
            }
        }

        public int GetSccGlyphFromStatus(uint dwSccStatus, VsStateIcon[] psiGlyph)
        {
            psiGlyph[0] = VsStateIcon.STATEICON_NOSTATEICON;
            return VSConstants.S_OK;
        }

        public int BrowseForProject(out string pbstrDirectory, out int pfOK) { pbstrDirectory = null; pfOK = 0; return VSConstants.E_NOTIMPL; }

        public int CancelAfterBrowseForProject() => VSConstants.E_NOTIMPL;
    }
}
