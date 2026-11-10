using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using SVNAssist.Services;

namespace SVNAssist.Commands;

/// <summary>
/// Base dei comandi del sottomenu SVN di Solution Explorer: controlla che svn sia installato,
/// ricava l'elemento cliccato e la sua working copy, gestisce gli errori in un unico punto.
/// </summary>
internal abstract class SvnContextCommand : Command
{
    /// <summary>Messaggio d'errore mostrato se l'operazione fallisce (es. "Errore durante l'update").</summary>
    protected abstract string ErrorTitle { get; }

    /// <summary>Esegue l'azione sull'elemento cliccato (già verificato come appartenente a una working copy).</summary>
    protected abstract Task RunAsync(SvnTarget target, SvnService svn, CancellationToken ct);

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        if (!await SvnSetupAssistant.EnsureSvnAvailableAsync(this.Extensibility, cancellationToken))
            return;

        try
        {
            var target = SvnTarget.Resolve((await context.GetSelectedPathAsync(cancellationToken))?.LocalPath);
            if (target is null)
            {
                await ShowAsync("Nessun elemento selezionato in Solution Explorer.", cancellationToken);
                return;
            }

            if (target.WorkingCopyRoot is null)
            {
                await ShowAsync($"{SvnTarget.NotWorkingCopyMessage}:\n{target.Path}", cancellationToken);
                return;
            }

            var svn = new SvnService(SvnLocator.FindSvn() ?? "svn.exe", new ProcessRunner());
            await RunAsync(target, svn, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Annullato dall'utente: nessun messaggio.
        }
        catch (Exception ex)
        {
            await ShowAsync($"{ErrorTitle}:\n{ex.Message}", cancellationToken);
        }
    }

    /// <summary>Mostra un messaggio con il solo pulsante OK.</summary>
    protected Task ShowAsync(string message, CancellationToken ct) =>
        this.Extensibility.Shell().ShowPromptAsync(message, PromptOptions.OK, ct);

    /// <summary>Chiede conferma (OK/Annulla); <c>true</c> se l'utente conferma.</summary>
    protected Task<bool> ConfirmAsync(string message, CancellationToken ct) =>
        this.Extensibility.Shell().ShowPromptAsync(message, PromptOptions.OKCancel, ct);
}
