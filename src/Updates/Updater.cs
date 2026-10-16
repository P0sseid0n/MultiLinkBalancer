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
using MultiLink.Common;
using MultiLink.Platform;

namespace MultiLink.Updates
{
    public class Updater
    {
        const string Repository = "P0sseid0n/MultiLinkBalancer";
        const string AssetName = "MultiLinkBalancer.exe";

        // estados enviados à interface
        public const string Idle = "idle", Checking = "checking", UpToDate = "uptodate", Available = "available", Downloading = "downloading", Failed = "error";

        public static readonly Version Current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        public volatile string State = Idle;
        public string Latest, Notes, Error;
        public volatile int Progress; // download, 0 a 100
        public DateTime LastCheck = DateTime.MinValue;
        string exeUrl, checksumUrl;

        static Version Normalize(Version version) { return new Version(version.Major, version.Minor, Math.Max(0, version.Build)); }

        static string ExePath { get { return Application.ExecutablePath; } }

        // Restos de uma atualização anterior (o .exe antigo só pode ser apagado depois que ele fecha).
        public static void Cleanup()
        {
            foreach (var leftover in new[] { ExePath + ".old", ExePath + ".new" })
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch { }
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
            if (State == Checking || State == Downloading) return State == Available;
            State = Checking;
            Error = null;
            LastCheck = DateTime.UtcNow;
            try
            {
                var release = await FetchLatestRelease();
                if (release == null || !release.ContainsKey("tag_name")) throw new Exception("resposta inesperada do GitHub");
                var tag = (string)release["tag_name"];
                Version latest;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out latest)) throw new Exception("versão publicada inválida: " + tag);
                latest = Normalize(latest);

                exeUrl = checksumUrl = null;
                foreach (var asset in ((object[])release["assets"]).OfType<Dictionary<string, object>>())
                {
                    var name = (string)asset["name"];
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) exeUrl = (string)asset["browser_download_url"];
                    if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) checksumUrl = (string)asset["browser_download_url"];
                }
                Latest = latest.ToString();
                Notes = release.ContainsKey("body") ? release["body"] as string : null;
                bool newer = latest > Current && exeUrl != null && checksumUrl != null;
                State = newer ? Available : UpToDate;
                return newer;
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                Error = response != null && response.StatusCode == HttpStatusCode.NotFound
                    ? "nenhuma versão publicada encontrada (o repositório precisa ser público e ter uma release)"
                    : "não foi possível consultar o GitHub: " + ex.Message;
                State = Failed;
                return false;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                State = Failed;
                return false;
            }
        }

        static async Task<Dictionary<string, object>> FetchLatestRelease()
        {
            var request = Request("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.Accept = "application/vnd.github+json";
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return new JavaScriptSerializer().DeserializeObject(await reader.ReadToEndAsync()) as Dictionary<string, object>;
        }

        // Baixa, confere e troca o executável. Só volta se der errado; se der certo, chama restart.
        public async Task Install(ActivityLog log, Action restart)
        {
            if (State != Available) throw new Exception("Nenhuma atualização disponível.");
            State = Downloading;
            Progress = 0;
            Error = null;
            string exe = ExePath, downloaded = exe + ".new", previous = exe + ".old";
            try
            {
                var expectedHash = await FetchExpectedHash();
                await DownloadTo(downloaded);
                if (HashOf(downloaded) != expectedHash) throw new Exception("o arquivo baixado não confere com o SHA-256 publicado");

                if (File.Exists(previous)) File.Delete(previous);
                File.Move(exe, previous);
                try { File.Move(downloaded, exe); }
                catch { File.Move(previous, exe); throw; } // devolve o executável atual
            }
            catch (UnauthorizedAccessException)
            {
                Fail(downloaded, "sem permissão para trocar o arquivo na pasta do MultiLink. Abra como administrador ou mova o app para uma pasta sua.");
                throw new Exception("Atualização não concluída: " + Error);
            }
            catch (Exception ex)
            {
                Fail(downloaded, ex.Message);
                throw new Exception("Atualização não concluída: " + Error);
            }

            log.Add("Atualizado para a versão " + Latest + ". Reiniciando o MultiLink...", ActivityLog.LinkUp);
            Process.Start(new ProcessStartInfo(exe, Elevation.WaitForThisProcessArgument) { UseShellExecute = false });
            restart();
        }

        async Task<string> FetchExpectedHash()
        {
            using (var response = await Request(checksumUrl).GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                // formato do sha256sum: "<hash>  <arquivo>"
                var hash = (await reader.ReadToEndAsync()).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                if (hash.Length != 64) throw new Exception("arquivo de verificação (SHA-256) inválido");
                return hash;
            }
        }

        async Task DownloadTo(string path)
        {
            using (var response = await Request(exeUrl).GetResponseAsync())
            using (var source = response.GetResponseStream())
            using (var destination = File.Create(path))
            {
                long total = response.ContentLength, done = 0;
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, read);
                    done += read;
                    if (total > 0) Progress = (int)(done * 100 / total);
                }
            }
        }

        static string HashOf(string path)
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        void Fail(string downloaded, string error)
        {
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { }
            Error = error;
            State = Available; // pode tentar de novo
        }

        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "MultiLinkBalancer/" + Current;
            request.Timeout = 30000;
            return request;
        }
    }
}
