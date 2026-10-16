// Ajustes em %APPDATA%\MultiLinkBalancer\config.ini, uma linha "chave=valor" por ajuste.
// Os ajustes fixos têm propriedades tipadas aqui. As chaves por link ("link.<id>", "known.<id>",
// "speed.<id>", "metric.<id>") e as de uso interno ("prev.*") usam o acesso genérico em quem as conhece.
// Os nomes das chaves são o formato do arquivo: não renomeie sem migrar o valor antigo.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiLink.Storage
{
    public class AppSettings
    {
        static readonly string FilePath = Path.Combine(AppPaths.Roaming, "config.ini");
        readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int separator = line.IndexOf('=');
                        if (separator > 0) settings.values[line.Substring(0, separator)] = line.Substring(separator + 1);
                    }
            }
            catch { }
            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Roaming);
                File.WriteAllLines(FilePath, values.Select(pair => pair.Key + "=" + pair.Value), Encoding.UTF8);
            }
            catch { }
        }

        // ------------------------------------------------------------ acesso genérico
        public string Get(string key, string fallback) { string value; return values.TryGetValue(key, out value) ? value : fallback; }
        public int GetInt(string key, int fallback) { int value; return int.TryParse(Get(key, null), out value) ? value : fallback; }
        public bool GetBool(string key, bool fallback) { var value = Get(key, null); return value == null ? fallback : value == "1"; }
        public bool Has(string key) { return values.ContainsKey(key); }
        public void Remove(string key) { values.Remove(key); }
        public List<string> KeysStartingWith(string prefix) { return values.Keys.Where(key => key.StartsWith(prefix)).ToList(); }

        public void Set(string key, object value)
        {
            values[key] = value is bool ? ((bool)value ? "1" : "0") : (value == null ? "" : value.ToString());
        }

        // ------------------------------------------------------------ proxy
        public int Port { get { return GetInt("port", 1080); } set { Set("port", value); } }
        public bool AllowLan { get { return GetBool("lan", false); } set { Set("lan", value); } }
        public bool UseSystemProxy { get { return GetBool("sysProxy", true); } set { Set("sysProxy", value); } }
        public bool SystemProxyApplied { get { return GetBool("proxyApplied", false); } set { Set("proxyApplied", value); } }
        public bool CaptureAllPrograms { get { return GetBool("capture", false); } set { Set("capture", value); } }
        public bool WasRunning { get { return GetBool("running", false); } set { Set("running", value); } }

        // ------------------------------------------------------------ distribuição e quedas
        public int BalanceMode { get { return GetInt("mode", 0); } set { Set("mode", value); } }
        public bool Affinity { get { return GetBool("affinity", false); } set { Set("affinity", value); } }
        public bool QualityAware { get { return GetBool("quality", true); } set { Set("quality", value); } }
        public bool AutoRoute { get { return GetBool("autoRoute", false); } set { Set("autoRoute", value); } }
        public bool SpeedMeasured { get { return GetBool("measured", false); } set { Set("measured", value); } }

        // ------------------------------------------------------------ avisos, atalhos e atualizações
        public bool Notify { get { return GetBool("notify", true); } set { Set("notify", value); } }
        public bool NotifySound { get { return GetBool("notifySound", true); } set { Set("notifySound", value); } }
        public bool Hotkeys { get { return GetBool("hotkeys", true); } set { Set("hotkeys", value); } }
        public bool AutoUpdate { get { return GetBool("autoUpdate", true); } set { Set("autoUpdate", value); } }
        public string UpdateNotified { get { return Get("updateNotified", ""); } set { Set("updateNotified", value); } }
    }
}
