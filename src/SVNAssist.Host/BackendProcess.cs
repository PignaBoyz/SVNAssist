using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using SVNAssist.Rpc;

namespace SVNAssist.Host
{
    /// <summary>
    /// Avvia SVNAssist.Backend.exe (cartella backend/ accanto all'assembly), parla StreamJsonRpc su stdio,
    /// lo lega a un Job Object (muore con VS) e lo riavvia se cade.
    /// </summary>
    internal sealed class BackendProcess : IDisposable
    {
        private const int MaxRestartsPerMinute = 4;

        private readonly IHost _host;
        private readonly object _gate = new object();
        private Process _proc;
        private JsonRpc _rpc;
        private IBackend _proxy;
        private Task _starting;
        private bool _disposed;
        private int _restarts;
        private DateTime _windowStart = DateTime.UtcNow;

        /// <summary>Il backend e' ripartito dopo un crash (la UI mostra un toast e rimanda i "ready").</summary>
        public event Action Restarted;
        /// <summary>Il backend non parte / e' caduto troppe volte: testo per l'utente.</summary>
        public event Action<string> Failed;

        public BackendProcess(IHost host) { _host = host; }

        public bool IsRunning { get { lock (_gate) return _proxy != null; } }

        public static string BackendExe =>
            Path.Combine(Path.GetDirectoryName(typeof(BackendProcess).Assembly.Location), "backend", "SVNAssist.Backend.exe");

        /// <summary>Ritorna il proxy IBackend, avviando il processo se serve. Lancia se non parte.</summary>
        public async Task<IBackend> GetAsync()
        {
            Task start;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(BackendProcess));
                if (_proxy != null) return _proxy;
                start = _starting ?? (_starting = Task.Run(Start));
            }
            try { await start.ConfigureAwait(false); }
            finally { lock (_gate) { if (_starting == start) _starting = null; } }
            lock (_gate) { return _proxy ?? throw new InvalidOperationException(Loc.T("Backend SVNAssist non avviato.")); }
        }

        private void Start()
        {
            var exe = BackendExe;
            if (!File.Exists(exe))
                throw new FileNotFoundException(Loc.T("Backend SVNAssist non trovato ({0}). Reinstalla l'estensione.", exe));

            var dotnetRoot = EnsureRuntime();

            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                },
                EnableRaisingEvents = true,
            };
            p.StartInfo.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot; // l'apphost trova cosi' anche un runtime installato in cartella utente
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Write("[backend] " + e.Data); };
            p.Start();
            JobObject.Assign(p);
            p.BeginErrorReadLine();

            var rpc = new JsonRpc(p.StandardInput.BaseStream, p.StandardOutput.BaseStream);
            rpc.AddLocalRpcTarget<IHost>(_host, null);
            var proxy = rpc.Attach<IBackend>();
            rpc.Disconnected += (s, e) => OnGone(p);
            p.Exited += (s, e) => OnGone(p);
            rpc.StartListening();

            lock (_gate) { _proc = p; _rpc = rpc; _proxy = proxy; }
            Log.Write("backend avviato pid " + p.Id);
        }

        private const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
        private static readonly string UserDotnetDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");

        /// <summary>Radice dotnet con Microsoft.NETCore.App 10.x (anche se dotnet non e' nel PATH), o null.</summary>
        private static string FindRuntimeRoot()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var exe in new[] { "dotnet", Path.Combine(pf, "dotnet", "dotnet.exe"), Path.Combine(UserDotnetDir, "dotnet.exe") })
            {
                if (exe != "dotnet" && !File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo(exe, "--list-runtimes")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var pr = Process.Start(psi))
                    {
                        var output = pr.StandardOutput.ReadToEnd();
                        pr.WaitForExit(10000);
                        var root = DotnetRuntime.FindRoot(output);
                        if (root != null) return root;
                    }
                }
                catch (Exception ex) { Log.Write("dotnet --list-runtimes (" + exe + "): " + ex.Message); }
            }
            return null;
        }

        /// <summary>Verifica il runtime .NET 10; se manca propone installazione automatica o pagina download.</summary>
        private static string EnsureRuntime()
        {
            var root = FindRuntimeRoot();
            if (root != null) return root;

            var choice = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                return PromptDialog.Show(
                    Loc.T("SVNAssist richiede il runtime .NET 10, che non e' installato.\n\n" +
                          "Installa automaticamente: parte l'installazione (winget, oppure lo script ufficiale dotnet-install) in una finestra visibile.\n\n" +
                          "Apri pagina download: scarica \".NET Runtime 10.x – Windows x64 installer\" (non l'SDK), installalo e riprova."),
                    new[] { Loc.T("Installa automaticamente"), Loc.T("Apri pagina download"), Loc.T("Annulla") });
            });

            if (choice == 1)
            {
                try { Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true }); } catch (Exception ex) { Log.Write("apri download: " + ex.Message); }
                throw new InvalidOperationException(Loc.T("Runtime .NET 10 mancante: installa \".NET Runtime 10.x – Windows x64 installer\" da {0} e riprova.", DownloadUrl));
            }
            if (choice != 0) throw new InvalidOperationException(Loc.T("Runtime .NET 10 mancante: SVNAssist non puo' avviare il servizio."));

            // Finestra console visibile; exit code ignorato: conta solo il ricontrollo.
            RunVisible("winget", "install --id Microsoft.DotNet.Runtime.10 -e --silent --accept-package-agreements --accept-source-agreements");
            root = FindRuntimeRoot();
            if (root == null)
            {
                RunVisible("powershell",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
                    "& ([scriptblock]::Create((Invoke-RestMethod https://dot.net/v1/dotnet-install.ps1))) -Runtime dotnet -Channel 10.0 -InstallDir '" + UserDotnetDir + "'\"");
                root = FindRuntimeRoot();
            }
            if (root == null)
                throw new InvalidOperationException(Loc.T("Installazione del runtime .NET 10 non riuscita: installa \".NET Runtime 10.x – Windows x64 installer\" da {0}", DownloadUrl));
            Log.Write("runtime .NET 10 in " + root);
            return root;
        }

        private static void RunVisible(string exe, string args)
        {
            try
            {
                using (var pr = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }))
                    pr.WaitForExit(15 * 60 * 1000);
            }
            catch (Exception ex) { Log.Write(exe + ": " + ex.Message); } // es. winget assente
        }

        private void OnGone(Process p)
        {
            bool restart;
            lock (_gate)
            {
                if (_proc != p) return; // gia' gestito
                _proc = null; _proxy = null;
                try { _rpc?.Dispose(); } catch { }
                _rpc = null;
                restart = !_disposed;
            }
            try { if (!p.HasExited) p.Kill(); } catch { }
            if (!restart) return;
            Log.Write("backend terminato, riavvio");

            if ((DateTime.UtcNow - _windowStart).TotalSeconds > 60) { _windowStart = DateTime.UtcNow; _restarts = 0; }
            if (++_restarts > MaxRestartsPerMinute)
            {
                Failed?.Invoke(Loc.T("Il servizio SVNAssist si è chiuso troppe volte. Riavvia Visual Studio o consulta il log."));
                return;
            }
            Task.Run(async () =>
            {
                try { await GetAsync().ConfigureAwait(false); Restarted?.Invoke(); }
                catch (Exception ex) { Failed?.Invoke(Loc.T("Impossibile riavviare il servizio SVNAssist: {0}", ex.Message)); }
            });
        }

        public void Dispose()
        {
            Process p; JsonRpc r;
            lock (_gate) { _disposed = true; p = _proc; r = _rpc; _proc = null; _rpc = null; _proxy = null; }
            try { r?.Dispose(); } catch { }
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        /// <summary>Un file al giorno in logs\host-aaaammgg.log, come il backend. Nome e pid: piu' istanze di VS (normale + Exp) scrivono nello stesso file.</summary>
        public static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNAssist", "logs", "host-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        private static readonly int Pid = Process.GetCurrentProcess().Id;

        public static void Write(string msg)
        {
            Debug.WriteLine("[SVNAssist] " + msg);
            try
            {
                lock (Gate)
                {
                    var path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    // Causa del log "fermo": File.AppendAllText apre con FileShare.Read, quindi se un'altra istanza di VS (o un lettore) tiene
                    // il file aperto la scrittura falliva e l'eccezione era inghiottita. Qui si condivide in lettura E scrittura.
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var bytes = new System.Text.UTF8Encoding(false).GetBytes(DateTime.Now.ToString("HH:mm:ss.fff ") + "[" + Pid + "] " + msg + Environment.NewLine);
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[SVNAssist] scrittura log host non riuscita: " + ex.Message); }
        }
    }

    /// <summary>Job Object con KILL_ON_JOB_CLOSE: se VS muore (anche male) il backend viene terminato dal sistema.</summary>
    internal static class JobObject
    {
        private static readonly IntPtr Job = Create();

        private static IntPtr Create()
        {
            try
            {
                var h = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(h, 9, ptr, (uint)len)) return IntPtr.Zero; // JobObjectExtendedLimitInformation
                }
                finally { Marshal.FreeHGlobal(ptr); }
                return h;
            }
            catch (Exception ex) { Log.Write("Job Object non creato: " + ex.Message); return IntPtr.Zero; }
        }

        public static void Assign(Process p)
        {
            if (Job == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(Job, p.Handle)) Log.Write("AssignProcessToJobObject fallita: " + Marshal.GetLastWin32Error());
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }
}
