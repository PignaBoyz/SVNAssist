using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SVNAssist.Spike
{
    [VisualStudioContribution]
    internal class SpikeExtension : Extension
    {
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
        };
    }

    [VisualStudioContribution]
    internal class SpikeCommand : Command
    {
        public override CommandConfiguration CommandConfiguration => new("%SVNAssist.Spike.Command%")
        {
            Placements = new[] { CommandPlacement.KnownPlacements.ExtensionsMenu },
        };

        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
        {
            // In-proc: si puo' usare direttamente l'API VSSDK.
            var shell = (IVsShell7)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsShell));
            await shell.LoadPackageAsync(new Guid(SpikePackage.PackageGuid));
            await SpikePackage.Instance.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            await SpikePackage.Instance.ShowToolWindowAsync(typeof(SpikeToolWindow), 0, true, ct);
        }
    }
}
