using StreamJsonRpc;

// stdout e' il canale RPC: niente Console.WriteLine; i log vanno su stderr.
var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), new Echo());
Console.Error.WriteLine($"EchoBackend pid={Environment.ProcessId} runtime={Environment.Version}");
await rpc.Completion;

sealed class Echo
{
    [JsonRpcMethod("echo")]
    public string EchoAsync(string text) =>
        $"echo: {text} (backend pid {Environment.ProcessId}, .NET {Environment.Version})";
}
