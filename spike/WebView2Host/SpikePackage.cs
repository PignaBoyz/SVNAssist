using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;

namespace SVNAssist.Spike
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuid)]
    [ProvideToolWindow(typeof(SpikeToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell", Orientation = ToolWindowOrientation.Left)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SpikePackage : AsyncPackage
    {
        public const string PackageGuid = "6d1f5c0e-6c0a-4a43-9b0e-5a1d0a7b2e11";
        public static SpikePackage Instance;

        internal static void Log(string msg)
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "spike-log.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
            }
            catch { }
        }

        protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            // Solo per il test non interattivo: apre la tool window senza click.
            if (Environment.GetEnvironmentVariable("SVNASSIST_SPIKE_AUTO") == "1")
            {
                // Non si attende qui: ShowToolWindowAsync dentro InitializeAsync blocca il caricamento del package.
                JoinableTaskFactory.RunAsync(async delegate
                {
                    try
                    {
                        Log("package init, apro la tool window tra 5s");
                        await Task.Delay(5000);
                        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                        await ShowToolWindowAsync(typeof(SpikeToolWindow), 0, true, DisposalToken);
                        Log("ShowToolWindowAsync completato");
                    }
                    catch (Exception ex) { Log("ERRORE show: " + ex); }
                }).FileAndForget("SVNAssist/spike");
            }
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id) => "SVNAssist Spike";
        protected override Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken ct) => Task.FromResult<object>(null);
    }
}
