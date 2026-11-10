using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace SVNAssist.Commands;

/// <summary>
/// Sottomenu "SVN" nei context menu di Solution Explorer (file, cartella, progetto, solution),
/// sul modello del menu di TortoiseSVN.
/// </summary>
/// <remarks>
/// GUID e ID dei menu contestuali provengono da <c>vsshlids.h</c> / <c>SharedCmdPlace.vsct</c>
/// (pacchetto Microsoft.VSSDK.BuildTools): guidSHLMainMenu {D309F791-903F-11D0-9EFC-00A0C911004F},
/// IDM_VS_CTXT_ITEMNODE 0x0430, IDM_VS_CTXT_FOLDERNODE 0x0431, IDM_VS_CTXT_PROJNODE 0x0402,
/// IDM_VS_CTXT_SOLNNODE 0x0413. Il sottomenu è agganciato a un gruppo per ciascun menu contestuale.
/// </remarks>
internal static class SolutionExplorerMenu
{
    private const string ShellMainMenuGuid = "D309F791-903F-11D0-9EFC-00A0C911004F";

    private const uint ItemNode = 0x0430;
    private const uint FolderNode = 0x0431;
    private const uint ProjectNode = 0x0402;
    private const uint SolutionNode = 0x0413;

    // Priorità alta: il sottomenu compare dopo i gruppi standard di VS.
    private const ushort GroupPriority = 0x0F00;

    private static readonly Guid ShellMainMenu = new(ShellMainMenuGuid);

    /// <summary>Il sottomenu "SVN".</summary>
    [VisualStudioContribution]
    internal static MenuConfiguration Menu => new("%SVNAssist.SeMenu.DisplayName%");

    /// <summary>Aggancia il sottomenu al context menu dei file.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration ItemGroup => new(GroupPlacement.VsctParent(ShellMainMenu, ItemNode, GroupPriority))
    {
        Children = [GroupChild.Menu(Menu)],
    };

    /// <summary>Aggancia il sottomenu al context menu delle cartelle.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration FolderGroup => new(GroupPlacement.VsctParent(ShellMainMenu, FolderNode, GroupPriority))
    {
        Children = [GroupChild.Menu(Menu)],
    };

    /// <summary>Aggancia il sottomenu al context menu dei progetti.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration ProjectGroup => new(GroupPlacement.VsctParent(ShellMainMenu, ProjectNode, GroupPriority))
    {
        Children = [GroupChild.Menu(Menu)],
    };

    /// <summary>Aggancia il sottomenu al context menu della solution.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration SolutionGroup => new(GroupPlacement.VsctParent(ShellMainMenu, SolutionNode, GroupPriority))
    {
        Children = [GroupChild.Menu(Menu)],
    };

    /// <summary>Sincronizzazione: Update, Commit.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration SyncCommands => new(GroupPlacement.Menu(Menu, 0))
    {
        Children = [GroupChild.Command<SeUpdateCommand>(), GroupChild.Command<SeCommitCommand>()],
    };

    /// <summary>Consultazione: Diff, Log.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration InspectCommands => new(GroupPlacement.Menu(Menu, 1))
    {
        Children = [GroupChild.Command<SeDiffCommand>(), GroupChild.Command<SeLogCommand>()],
    };

    /// <summary>Modifica: Add, Revert, ignore list.</summary>
    [VisualStudioContribution]
    internal static CommandGroupConfiguration EditCommands => new(GroupPlacement.Menu(Menu, 2))
    {
        Children = [GroupChild.Command<SeAddCommand>(), GroupChild.Command<SeRevertCommand>(), GroupChild.Command<SeIgnoreCommand>()],
    };
}
