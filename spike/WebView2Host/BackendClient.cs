using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace SVNAssist.Spike
{
    /// <summary>Avvia il sidecar net8 e parla JSON-RPC su stdin/stdout.</summary>
    internal sealed class BackendClient : IDisposable
    {
        private Process _proc;
        private JsonRpc _rpc;

        public Task<string> EchoAsync(string text)
        {
            if (_rpc == null) Start();
            return _rpc.InvokeAsync<string>("echo", text);
        }

        private void Start()
        {
            var dir = Path.GetDirectoryName(typeof(BackendClient).Assembly.Location);
            var exe = Path.Combine(dir, "backend", "EchoBackend.exe");
            _proc = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            _proc.ErrorDataReceived += (s, e) => Debug.WriteLine("[backend] " + e.Data);
            _proc.BeginErrorReadLine();
            _rpc = JsonRpc.Attach(_proc.StandardInput.BaseStream, _proc.StandardOutput.BaseStream);
        }

        public void Dispose()
        {
            try { _rpc?.Dispose(); } catch { }
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            _proc?.Dispose();
        }
    }
}
