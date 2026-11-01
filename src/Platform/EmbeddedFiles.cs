// Arquivos embutidos no .exe pelo build.ps1 (/resource) e extraídos para %LOCALAPPDATA% quando precisos.
using System;
using System.IO;
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
            using (var source = Open("vigia.exe"))
            using (var sha = SHA256.Create())
            {
                if (source == null) throw new FileNotFoundException("Recurso embutido ausente: vigia.exe");
                hash = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
            var folder = Path.Combine(AppPaths.Local, "bin", "vigia-" + hash);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, exeName);
            Extract("vigia.exe", target);
            return target;
        }

        public static Stream Open(string resource)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        }

        // Só regrava se o arquivo não existe ou mudou de tamanho.
        static void Extract(string resource, string target)
        {
            using (var source = Open(resource))
            {
                if (source == null) throw new FileNotFoundException("Recurso embutido ausente: " + resource);
                if (File.Exists(target) && new FileInfo(target).Length == source.Length) return;
                using (var destination = File.Create(target)) source.CopyTo(destination);
            }
        }
    }
}
