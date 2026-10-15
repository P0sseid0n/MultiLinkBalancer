using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("MultiLink Balancer")]
[assembly: AssemblyProduct("MultiLink Balancer")]
[assembly: AssemblyVersion("2.0.0.0")]

namespace MultiLink
{
    static class Program
    {
        // As DLLs do WebView2 vão embutidas no .exe e são extraídas para cá na primeira execução.
        static string binDir;
        public const string ShowEventName = "Local\\MultiLinkBalancer.Show";

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            // reinício como administrador: espera a instância anterior fechar e liberar o mutex
            var wait = args.FirstOrDefault(a => a.StartsWith("/waitpid:", StringComparison.OrdinalIgnoreCase));
            int pid;
            if (wait != null && int.TryParse(wait.Substring(9), out pid))
                try { Process.GetProcessById(pid).WaitForExit(15000); } catch { }

            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor v2: UI nítida em telas com escala
            catch { try { SetProcessDPIAware(); } catch { } }

            bool created;
            using (var mutex = new Mutex(true, "Local\\MultiLinkBalancer.SingleInstance", out created))
            {
                if (!created)
                {
                    // já está aberto (talvez na bandeja): pede para a instância existente mostrar a janela
                    EventWaitHandle show;
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out show)) using (show) show.Set();
                    else MessageBox.Show("O MultiLink Balancer já está em execução (veja a bandeja do sistema).", "MultiLink Balancer");
                    return;
                }
                PrepareWebView2();
                ServicePointManager.DefaultConnectionLimit = 1000;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Run(args.Any(a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase)));
            }
        }

        // Separado do Main para que os tipos do WebView2 só sejam carregados depois do AssemblyResolve.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run(bool tray)
        {
            Application.Run(new HostForm(tray));
        }

        static void PrepareWebView2()
        {
            var asm = Assembly.GetExecutingAssembly();
            binDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiLinkBalancer", "bin", "1.0.4258.31");
            Directory.CreateDirectory(binDir);
            Extract(asm, "wv2.core.dll", Path.Combine(binDir, "Microsoft.Web.WebView2.Core.dll"));
            Extract(asm, "wv2.loader.dll", Path.Combine(binDir, "WebView2Loader.dll"));
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                new AssemblyName(e.Name).Name == "Microsoft.Web.WebView2.Core"
                    ? Assembly.LoadFrom(Path.Combine(binDir, "Microsoft.Web.WebView2.Core.dll")) : null;
            SetLoaderFolder();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetLoaderFolder()
        {
            Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(binDir);
        }

        // tun2socks + wintun.dll (captura de todos os programas): extraídos só quando a captura é ligada.
        public static string ExtractTun2Socks(string exeName)
        {
            var asm = Assembly.GetExecutingAssembly();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiLinkBalancer", "bin", "tun2socks-2.7.0");
            Directory.CreateDirectory(dir);
            Extract(asm, "t2s.exe", Path.Combine(dir, exeName));
            Extract(asm, "t2s.wintun.dll", Path.Combine(dir, "wintun.dll"));
            return dir;
        }

        static void Extract(Assembly asm, string resource, string target)
        {
            using (var src = asm.GetManifestResourceStream(resource))
            {
                if (src == null) throw new FileNotFoundException("Recurso embutido ausente: " + resource);
                if (File.Exists(target) && new FileInfo(target).Length == src.Length) return;
                using (var dst = File.Create(target)) src.CopyTo(dst);
            }
        }
    }
}
