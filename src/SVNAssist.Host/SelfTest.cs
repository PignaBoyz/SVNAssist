using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SVNAssist.Host
{
    /// <summary>Solo per la verifica end-to-end: attivo con SVNASSIST_SELFTEST=1 (spento di default).
    /// Al primo state di "changes" con un non versionato: stage, messaggio "selftest", commit; esito nel host-log.</summary>
    internal static class SelfTest
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("SVNASSIST_SELFTEST") == "1";
        private static int _started;

        public static void OnPush(HostService host, string view, string json)
        {
            try
            {
                var m = JObject.Parse(json);
                var type = (string)m["type"];
                if (type == "reply") { Log.Write("SELFTEST reply " + json); return; }
                if (type != "state" || view != "changes") return;
                var s = m["state"] ?? m;
                var secs = (s["sections"] as JArray)?.Select(x => (string)x["id"] + "=" + (int?)x["count"]);
                Log.Write("SELFTEST state loading=" + s["loading"] + " sections=" + (secs == null ? "-" : string.Join(",", secs)) + " status=" + s["status"]);
                var key = (s["sections"] as JArray)?.FirstOrDefault(x => (string)x["id"] == "unversioned")?["rows"]?.FirstOrDefault(r => (string)r["kind"] == "file")?["key"];
                if (key == null || (bool?)s["loading"] == true || System.Threading.Interlocked.Exchange(ref _started, 1) == 1) return;
                _ = Task.Run(async () =>
                {
                    Log.Write("SELFTEST stage " + key);
                    await host.SendCommandAsync("changes", "stage", new JObject { ["key"] = key });
                    await host.SendCommandAsync("changes", "setCommitText", new JObject { ["text"] = "selftest" });
                    await Task.Delay(1500);
                    Log.Write("SELFTEST commit");
                    await host.SendCommandAsync("changes", "commit", null);
                });
            }
            catch (Exception ex) { Log.Write("SELFTEST errore " + ex.Message); }
        }
    }
}
