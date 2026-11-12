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
}
