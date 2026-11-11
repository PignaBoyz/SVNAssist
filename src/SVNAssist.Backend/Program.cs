using SVNAssist;
using SVNAssist.Rpc;
using StreamJsonRpc;

// stdout e' il canale JSON-RPC verso l'host (SVNAssist.Host): niente Console.WriteLine. Le scritture accidentali sulla
// console (anche di librerie di terzi) vengono deviate su stderr, cosi' non corrompono il flusso.
var stdout = Console.OpenStandardOutput();
var stdin = Console.OpenStandardInput();
Console.SetOut(Console.Error);

BackendLog.Prune();
BackendLog.Info($"Avvio backend pid={Environment.ProcessId} runtime={Environment.Version}");
AppDomain.CurrentDomain.UnhandledException += (_, e) => BackendLog.Error("Eccezione non gestita", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    BackendLog.Error("Task non osservato", e.Exception);
    e.SetObserved();
};

// Stessi default dell'host (formatter e metodi con il nome C# dell'interfaccia): host -> IBackend, backend -> IHost.
var rpc = new JsonRpc(stdout, stdin);
var host = rpc.Attach<IHost>();
using var server = new BackendServer(host, BackendServices.CreateDefault(host));
rpc.AddLocalRpcTarget<IBackend>(server, null);
rpc.StartListening();

// Termina quando l'host chiude la pipe (anche se VS muore).
await rpc.Completion;
BackendLog.Info("Connessione chiusa, backend terminato.");
