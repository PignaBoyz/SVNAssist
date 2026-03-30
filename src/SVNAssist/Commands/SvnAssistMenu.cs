using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace SVNAssist.Commands;

/// <summary>
/// Definisce il sottomenu "SVNAssist" nel menu Extensions di Visual Studio.
/// </summary>
/// <remarks>
/// Nel modello VisualStudio.Extensibility, i menu si definiscono tramite
/// <see cref="MenuConfiguration"/> con <c>[VisualStudioContribution]</c>.
/// Il source generator li registra automaticamente.
///
/// Struttura risultante:
/// <code>
/// Extensions
///   └── SVNAssist ▸
///         ├── SVN Changes
///         ├── Commit...
///         ├── ──────────
///         ├── Update
///         └── Log / History
/// </code>
///
/// Nel vecchio modello VSSDK si usavano file <c>.vsct</c> con GUID/ID
/// e <c>OleMenuCommandService</c> per definire la stessa struttura.
/// </remarks>
internal static class SvnAssistMenu
{
    /// <summary>
    /// Configurazione del menu SVNAssist che appare sotto Extensions.
    /// </summary>
    [VisualStudioContribution]
    internal static MenuConfiguration Menu => new("%SVNAssist.Menu.DisplayName%");

    /// <summary>
    /// Gruppo che posiziona il menu SVNAssist dentro il menu Extensions.
    /// </summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration MenuGroup => new(
        GroupPlacement.KnownPlacements.ExtensionsMenu)
    {
        Children =
        [
            GroupChild.Menu(Menu),
        ],
    };

    /// <summary>
    /// Gruppo con i comandi principali (SVN Changes, Commit).
    /// </summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration PrimaryCommands => new(
        GroupPlacement.Menu(Menu, 0))
    {
        Children =
        [
            GroupChild.Command<StatusCommand>(),
            GroupChild.Command<CommitCommand>(),
        ],
    };

    /// <summary>
    /// Gruppo con i comandi secondari (Update, Log), separato da una linea.
    /// </summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration SecondaryCommands => new(
        GroupPlacement.Menu(Menu, 1))
    {
        Children =
        [
            GroupChild.Command<UpdateCommand>(),
            GroupChild.Command<ShowLogCommand>(),
        ],
    };
}
