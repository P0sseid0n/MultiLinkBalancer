// Arquivos embutidos no .exe pelo build.ps1 (/resource) e extraídos para %LOCALAPPDATA% quando precisos.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using MultiLink.Storage;

namespace MultiLink.Platform
{
    public static class EmbeddedFiles
    {
        public const string WebView2Core = "Microsoft.Web.WebView2.Core.dll";
        public const string WebView2Loader = "WebView2Loader.dll";

        // A pasta leva a versão do componente: atualizar a DLL embutida exige mudar o nome.
        public static string ExtractWebView2()
        {
            var folder = Path.Combine(AppPaths.Local, "bin", "1.0.4258.31");
            Directory.CreateDirectory(folder);
            Extract("wv2.core.dll", Path.Combine(folder, WebView2Core));
            Extract("wv2.loader.dll", Path.Combine(folder, WebView2Loader));
            return folder;
        }

        // tun2socks + wintun.dll (captura de todos os programas): extraídos só quando a captura é ligada.
        public static string ExtractTun2Socks(string exeName)
        {
            var folder = Path.Combine(AppPaths.Local, "bin", "tun2socks-2.7.0");
            Directory.CreateDirectory(folder);
            Extract("t2s.exe", Path.Combine(folder, exeName));
            Extract("t2s.wintun.dll", Path.Combine(folder, "wintun.dll"));
            return folder;
        }

        // Vigia do proxy (ProxyWatchdog). A pasta leva o hash do arquivo: o vigia da versão anterior pode ainda estar
        // aberto (e o .exe dele, travado) quando esta versão abre, e um vigia novo do mesmo tamanho não seria regravado.
        public static string ExtractWatchdog(string exeName)
        {
            string hash;
            using (var source = Open("watchdog.exe"))
            using (var sha = SHA256.Create())
            {
                if (source == null) throw new FileNotFoundException("Recurso embutido ausente: watchdog.exe");
                hash = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
            var bin = Path.Combine(AppPaths.Local, "bin");
            var folder = Path.Combine(bin, "watchdog-" + hash);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, exeName);
            Extract("watchdog.exe", target);
            // as das versões anteriores (vigia-*: quando se chamava multilink-vigia.exe); a de um vigia ainda aberto
            // falha agora e sai na próxima abertura
            foreach (var old in Directory.GetDirectories(bin, "watchdog-*").Concat(Directory.GetDirectories(bin, "vigia-*")))
                if (!string.Equals(old, folder, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(old, true); } catch { }
            return target;
        }

        public static Stream Open(string resource)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        }

        // Arquivos conferidos, abertos só para leitura sem permitir gravar, apagar nem renomear (nem a pasta) até o
        // processo fechar. Conferir e depois fechar deixaria outro programa trocar o arquivo entre a conferência e o uso
        // (o loader do WebView2 só é carregado quando o painel abre, às vezes horas depois).
        static readonly Dictionary<string, FileStream> locked = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

        // Só regrava se o arquivo não existe ou não é igual ao embutido. Confere o conteúdo (SHA-256), não só o tamanho:
        // a pasta é do usuário, e o que sai dela roda com o MultiLink, muitas vezes como administrador. Um arquivo
        // trocado por outro programa é regravado antes de ser usado.
        static void Extract(string resource, string target)
        {
            lock (locked)
            {
                if (locked.ContainsKey(target)) return; // já conferido e travado nesta execução
                byte[] expected;
                using (var source = Open(resource))
                using (var sha = SHA256.Create())
                {
                    if (source == null) throw new FileNotFoundException("Recurso embutido ausente: " + resource);
                    expected = sha.ComputeHash(source);
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        var existing = OpenLocked(target);
                        if (existing != null)
                        {
                            // o hash é do mesmo handle que fica aberto: o que foi conferido é o que será usado
                            if (existing.Length == source.Length && expected.SequenceEqual(sha.ComputeHash(existing))) { locked[target] = existing; return; }
                            existing.Dispose();
                        }
                        if (attempt == 1) break;
                        source.Position = 0;
                        using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
                    }
                }
                throw new IOException("O arquivo extraído não confere com o embutido: " + target);
            }
        }

        static FileStream OpenLocked(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { return null; } // não deu para ler: tenta regravar
        }
    }
}
