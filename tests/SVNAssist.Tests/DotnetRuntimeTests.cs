using SVNAssist.Rpc;

namespace SVNAssist.Tests;

public class DotnetRuntimeTests
{
    [Fact]
    public void FindRoot_sceglie_il_runtime_10_piu_recente()
    {
        var o = "Microsoft.AspNetCore.App 10.0.0 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]\r\n" +
                "Microsoft.NETCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\r\n" +
                "Microsoft.NETCore.App 10.0.1 [C:\\Users\\a\\AppData\\Local\\Microsoft\\dotnet\\shared\\Microsoft.NETCore.App]\r\n" +
                "Microsoft.NETCore.App 10.0.2 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\r\n";
        Assert.Equal("C:\\Program Files\\dotnet", DotnetRuntime.FindRoot(o));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Microsoft.NETCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]")]
    [InlineData("Microsoft.NETCore.App 11.0.0 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]")]
    [InlineData("Microsoft.WindowsDesktop.App 10.0.0 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]")]
    public void FindRoot_null_senza_runtime_10(string? output) => Assert.Null(DotnetRuntime.FindRoot(output));
}
