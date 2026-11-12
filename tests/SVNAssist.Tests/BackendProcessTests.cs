using System.Diagnostics;
using System.Text.Json.Nodes;
using SVNAssist.Rpc;
using StreamJsonRpc;

namespace SVNAssist.Tests;

/// <summary>
/// Il vero eseguibile del backend, pilotato come fa l'host: StreamJsonRpc su stdin/stdout, IBackend chiamato via proxy,
/// IHost implementato qui. Verifica il cablaggio (nomi dei metodi, formatter) e che stdout resti solo canale RPC.
/// </summary>
public sealed class BackendProcessTests
{
    private sealed class HostStub : IHost
    {
        public TaskCompletionSource<JsonNode> FirstState { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, string> Settings { get; } = new() { ["aiProvider"] = "claude", ["workingCopyPath"] = "" };

        public Task PushAsync(string view, string messageJson)
        {
            var node = JsonNode.Parse(messageJson)!;
            if (view == "changes" && node["type"]!.GetValue<string>() == "state")
                FirstState.TrySetResult(node);
            return Task.CompletedTask;
        }

        public Task<int> PromptAsync(string message, string[] buttons) => Task.FromResult(-1);
        public Task<string?> PromptTextAsync(string message, string label, string defaultValue) => Task.FromResult<string?>(null);
        public Task<string[]?> PromptCredentialsAsync(string detail, string defaultUser) => Task.FromResult<string[]?>(null);
        public Task<Dictionary<string, string>> GetSettingsAsync() => Task.FromResult(Settings);
        public Task SetSettingAsync(string key, string value) => Task.CompletedTask;
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task OpenSettingsAsync() => Task.CompletedTask;
        public Task OpenDiffAsync(string l, string r, string ll, string rl, string c) => Task.CompletedTask;
        public Task OpenFileAsync(string path) => Task.CompletedTask;
        public Task ShowLogAsync(string path) => Task.CompletedTask;
        public Task ShowLogWindowAsync(string title) => Task.CompletedTask;
        public Task ShowChangesWindowAsync() => Task.CompletedTask;
        public Task<bool> ApplyDocumentTextAsync(string path, string text) => Task.FromResult(false);
        public Task ShowFolderDiffAsync(string path) => Task.CompletedTask;
        public Task RunInTerminalAsync(string command, string workingDir) => Task.CompletedTask;
    }

    [Fact]
    public async Task Exe_ParlaStreamJsonRpc_RispondeAReadyConUnoSnapshot_ETerminaAllaChiusuraDellaPipe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "SVNAssist.Backend.exe");
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var host = new HostStub();
        using var rpc = new JsonRpc(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, host);
        var backend = rpc.Attach<IBackend>();
        rpc.StartListening();

        await backend.ThemeChangedAsync("light");
        await backend.DispatchAsync("changes", """{"v":1,"type":"ready","view":"changes","id":null}""");
        var state = (await host.FirstState.Task.WaitAsync(TimeSpan.FromSeconds(30)))["state"]!;

        Assert.Equal("light", state["theme"]!["kind"]!.GetValue<string>());
        Assert.Equal(3, state["sections"]!.AsArray().Count);

        // Chiusura della pipe (come quando muore VS): il backend termina da solo.
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(15000), "il backend doveva terminare alla chiusura di stdin");
        Assert.True(Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "logs")), "il log deve andare su file in %LOCALAPPDATA%");
    }
}
