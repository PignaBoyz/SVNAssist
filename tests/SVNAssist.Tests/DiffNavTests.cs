using SVNAssist.Navigation;

namespace SVNAssist.Tests;

/// <summary>Prossimo/precedente blocco modificato (DiffNav, logica pura).</summary>
public class DiffNavTests
{
    private static readonly int[] Starts = [10, 3, 25]; // volutamente non ordinato

    [Theory]
    [InlineData(1, 3)]
    [InlineData(3, 10)]
    [InlineData(5, 10)]
    [InlineData(10, 25)]
    [InlineData(25, 3)]  // giro all'inizio
    [InlineData(99, 3)]
    public void Next(int current, int expected) => Assert.Equal(expected, DiffNav.Next(Starts, current));

    [Theory]
    [InlineData(99, 25)]
    [InlineData(25, 10)]
    [InlineData(11, 10)]
    [InlineData(10, 3)]
    [InlineData(3, 25)]  // giro alla fine
    [InlineData(1, 25)]
    public void Previous(int current, int expected) => Assert.Equal(expected, DiffNav.Previous(Starts, current));

    [Fact]
    public void NoBlocks()
    {
        Assert.Equal(0, DiffNav.Next([], 5));
        Assert.Equal(0, DiffNav.Previous([], 5));
    }

    [Fact]
    public void SingleBlockWrapsOnItself()
    {
        Assert.Equal(7, DiffNav.Next([7], 7));
        Assert.Equal(7, DiffNav.Previous([7], 7));
    }

    [Theory]
    [InlineData("Global::Alt+F8", "alt+f8")]
    [InlineData("Globale::Shift+Alt+F8", "shift+alt+f8")]
    [InlineData("Alt+Shift+F8", "shift+alt+f8")]
    [InlineData("Control + Alt + K", "ctrl+alt+k")]
    [InlineData("  ", "")]
    public void ShortcutBinding_NormalizzaCombinazioniIndipendentementeDalloScope(string input, string expected)
        => Assert.Equal(expected, ShortcutBinding.NormalizeKeyCombo(input));

    [Fact]
    public void ShortcutBinding_ConfrontoEScopeFunzionanoAncheLocalizzati()
    {
        Assert.True(ShortcutBinding.MatchesAny(["Global::Alt+Shift+F8"], "Shift+Alt+F8"));
        Assert.True(ShortcutBinding.MatchesAny(["Text Editor::Ctrl+K", "Globale::Alt+F8"], "Alt+F8"));
        Assert.False(ShortcutBinding.MatchesAny(["Global::Alt+F8"], "Ctrl+F8"));

        Assert.Equal("Globale::", ShortcutBinding.ExtractScopePrefix(["Globale::Ctrl+Shift+S"]));
        Assert.Equal("Global::", ShortcutBinding.ExtractScopePrefix([]));

        Assert.Equal("Globale::Alt+F8", ShortcutBinding.WithScope("Globale::", "Alt+F8"));
        Assert.Equal("Editor di testo::Ctrl+K", ShortcutBinding.WithScope("Global::", "Editor di testo::Ctrl+K"));
        Assert.Equal("Shift+Alt+F8", ShortcutBinding.Canonicalize("Alt+Shift+F8"));
    }
}
