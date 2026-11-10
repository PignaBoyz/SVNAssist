using System.Diagnostics;

namespace SVNAssist.Services;

/// <summary>Comandi interattivi che SVNAssist sa lanciare in una finestra console visibile.</summary>
internal enum ConsoleCommand
{
    /// <summary>Installa il client a riga di comando di Subversion (SlikSVN) con winget.</summary>
    InstallSvn,

    /// <summary>Installa GitHub CLI con winget.</summary>
    InstallGitHubCli,

    /// <summary>Login a GitHub con il flusso da browser (nessun prompt da compilare).</summary>
    GitHubLogin,

    /// <summary>Installa Claude Code con lo script ufficiale PowerShell.</summary>
    InstallClaudeCode,

    /// <summary>Avvia <c>claude</c>: al primo avvio chiede il login.</summary>
    ClaudeLogin,
}

/// <summary>
/// Lancia comandi interattivi (winget, gh, claude) in una console visibile, così l'utente
/// non deve aprire un terminale a mano.
/// </summary>
/// <remarks>
/// La riga di comando è costruita solo da costanti (mai da input utente o impostazioni):
/// l'unico valore variabile è il percorso di <c>claude.exe</c> trovato sul disco.
/// La costruzione è una funzione pura (<see cref="Build"/>) testabile; <see cref="Launch"/> è minimale.
/// </remarks>
internal static class ConsoleLauncher
{
    /// <summary>Costruisce eseguibile e argomenti per il comando richiesto.</summary>
    /// <param name="command">Comando da lanciare.</param>
    /// <param name="claudeExePath">Percorso di <c>claude.exe</c> (solo per <see cref="ConsoleCommand.ClaudeLogin"/>).</param>
    internal static (string FileName, string Arguments) Build(ConsoleCommand command, string? claudeExePath = null)
        => command switch
        {
            // cmd /k lascia la finestra aperta a fine comando: l'utente vede l'esito.
            ConsoleCommand.InstallSvn => ("cmd.exe",
                "/k winget install --id Slik.Subversion -e --accept-package-agreements --accept-source-agreements"),
            ConsoleCommand.InstallGitHubCli => ("cmd.exe",
                "/k winget install --id GitHub.cli -e --accept-package-agreements --accept-source-agreements"),
            ConsoleCommand.GitHubLogin => ("cmd.exe",
                "/k gh auth login --hostname github.com --git-protocol https --web"),
            ConsoleCommand.InstallClaudeCode => ("powershell.exe",
                "-NoExit -Command \"irm https://claude.ai/install.ps1 | iex\""),
            ConsoleCommand.ClaudeLogin => ("cmd.exe",
                string.IsNullOrWhiteSpace(claudeExePath) ? "/k claude" : $"/k \"\"{claudeExePath}\"\""),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    /// <summary>Avvia il comando in una finestra visibile. <c>false</c> se non è stato possibile avviarla.</summary>
    internal static bool Launch(ConsoleCommand command, string? claudeExePath = null)
    {
        try
        {
            var (file, args) = Build(command, claudeExePath);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
