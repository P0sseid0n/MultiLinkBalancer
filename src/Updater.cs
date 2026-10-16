// Atualização pelo GitHub Releases: consulta a última versão publicada, baixa o .exe, confere o
// SHA-256 publicado junto (MultiLinkBalancer.exe.sha256) e troca o executável. O Windows deixa
// renomear um .exe em uso, então o atual vira ".old", o novo assume o nome e o app reinicia.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MultiLink
{
    public class Updater
    {
        const string Repo = "P0sseid0n/MultiLinkBalancer";
        const string AssetName = "MultiLinkBalancer.exe";

        public static readonly Version Current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        // idle | checking | uptodate | available | downloading | error
        public volatile string State = "idle";
        public string Latest, Notes, Error;
        public volatile int Progress; // download, 0 a 100
        public DateTime LastCheck = DateTime.MinValue;
        string exeUrl, shaUrl;

        static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        static string ExePath { get { return Application.ExecutablePath; } }

        // Restos de uma atualização anterior (o .exe antigo só pode ser apagado depois que ele fecha).
        public static void Cleanup()
        {
            foreach (var f in new[] { ExePath + ".old", ExePath + ".new" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        public Dictionary<string, object> ToState()
        {
            return new Dictionary<string, object>
            {
                { "current", Current.ToString() }, { "latest", Latest }, { "state", State },
                { "progress", Progress }, { "error", Error }, { "notes", Notes }
            };
        }

        // Devolve true se há uma versão mais nova.
        public async Task<bool> Check()
        {
            if (State == "checking" || State == "downloading") return State == "available";
            State = "checking";
            Error = null;
            LastCheck = DateTime.UtcNow;
            try
            {
                var req = Request("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.Accept = "application/vnd.github+json";
                string body;
                using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                using (var r = new StreamReader(resp.GetResponseStream())) body = await r.ReadToEndAsync();

                var rel = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (rel == null || !rel.ContainsKey("tag_name")) throw new Exception("resposta inesperada do GitHub");
                var tag = ((string)rel["tag_name"]).TrimStart('v', 'V');
                Version latest;
                if (!Version.TryParse(tag, out latest)) throw new Exception("versão publicada inválida: " + rel["tag_name"]);
                latest = Normalize(latest);

                exeUrl = shaUrl = null;
                foreach (var a in ((object[])rel["assets"]).OfType<Dictionary<string, object>>())
                {
                    var name = (string)a["name"];
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) exeUrl = (string)a["browser_download_url"];
                    if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = (string)a["browser_download_url"];
                }
                Latest = latest.ToString();
                Notes = rel.ContainsKey("body") ? rel["body"] as string : null;
                bool newer = latest > Current && exeUrl != null && shaUrl != null;
                State = newer ? "available" : "uptodate";
                return newer;
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                Error = r != null && r.StatusCode == HttpStatusCode.NotFound
                    ? "nenhuma versão publicada encontrada (o repositório precisa ser público e ter uma release)"
                    : "não foi possível consultar o GitHub: " + ex.Message;
                State = "error";
                return false;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                State = "error";
                return false;
            }
        }

        // Baixa, confere e troca o executável. Só volta se der errado; se der certo, chama restart.
        public async Task Install(Action<string, string> log, Action restart)
        {
            if (State != "available") throw new Exception("Nenhuma atualização disponível.");
            State = "downloading";
            Progress = 0;
            Error = null;
            string exe = ExePath, fresh = exe + ".new", old = exe + ".old";
            try
            {
                string expected;
                using (var resp = await Request(shaUrl).GetResponseAsync())
                using (var r = new StreamReader(resp.GetResponseStream()))
                    expected = (await r.ReadToEndAsync()).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                if (expected.Length != 64) throw new Exception("arquivo de verificação (SHA-256) inválido");

                using (var resp = await Request(exeUrl).GetResponseAsync())
                using (var src = resp.GetResponseStream())
                using (var dst = File.Create(fresh))
                {
                    long total = resp.ContentLength, done = 0;
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        done += n;
                        if (total > 0) Progress = (int)(done * 100 / total);
                    }
                }

                string actual;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(fresh))
                    actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new Exception("o arquivo baixado não confere com o SHA-256 publicado");

                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Move(fresh, exe); }
                catch { File.Move(old, exe); throw; } // devolve o executável atual
            }
            catch (UnauthorizedAccessException)
            {
                Fail(fresh, "sem permissão para trocar o arquivo na pasta do MultiLink. Abra como administrador ou mova o app para uma pasta sua.");
                throw new Exception("Atualização não concluída: " + Error);
            }
            catch (Exception ex)
            {
                Fail(fresh, ex.Message);
                throw new Exception("Atualização não concluída: " + Error);
            }

            log("Atualizado para a versão " + Latest + ". Reiniciando o MultiLink...", "up");
            Process.Start(new ProcessStartInfo(exe, "/waitpid:" + Process.GetCurrentProcess().Id) { UseShellExecute = false });
            restart();
        }

        void Fail(string fresh, string error)
        {
            try { if (File.Exists(fresh)) File.Delete(fresh); } catch { }
            Error = error;
            State = "available"; // pode tentar de novo
        }

        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "MultiLinkBalancer/" + Current;
            req.Timeout = 30000;
            return req;
        }
    }
}
