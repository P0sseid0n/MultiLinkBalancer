// Entrada do app: instância única, DPI e carregamento do WebView2 embutido. Ver ARCHITECTURE.md.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using System.Windows.Forms;
using MultiLink.Common;
using MultiLink.Platform;
using MultiLink.Ui;

[assembly: AssemblyTitle("MultiLink Balancer")]
[assembly: AssemblyProduct("MultiLink Balancer")]
[assembly: AssemblyCompany("P0sseid0n")] // "Fornecedor" em Aplicativos de inicialização do Gerenciador de Tarefas
// AssemblyVersion vem do build.ps1 (tag do git ou -Version): é a versão comparada nas atualizações

namespace MultiLink
{
    static class Program
    {
        const string SingleInstanceMutex = "Local\\MultiLinkBalancer.SingleInstance";
        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        static string webView2Folder;

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            StartupTimeline.Mark("carregar o .NET");
            bool launcher = HasArgument(args, StartupRegistration.LauncherArgument);
            bool background = launcher || HasArgument(args, StartupRegistration.TrayArgument);
            // desativado em "Aplicativos de inicialização": a tarefa agendada continua rodando no logon, mas o app não abre
            if (background && StartupRegistration.DisabledInTaskManager) return;
            // entrada "Run": a tarefa já abriu o app (o normal) ou ela abre agora; sem tarefa, segue sem elevação
            if (launcher && (InstanceRunning() || StartupRegistration.RunTask())) return;
            WaitForPreviousInstance(args);
            // per-monitor v2: interface nítida em telas com escala
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { try { SetProcessDPIAware(); } catch { } }

            bool firstInstance;
            using (new Mutex(true, SingleInstanceMutex, out firstInstance))
            {
                if (!firstInstance) { if (!background) ShowExistingInstance(); return; }
                PrepareWebView2();
                StartupTimeline.Mark("instância única e WebView2");
                ServicePointManager.DefaultConnectionLimit = 1000;
                // O DNS do .NET Framework ocupa uma thread do pool até responder. Numa rajada de conexões (página
                // pesada) o pool esgota e só ganha uma thread nova a cada ~0,5 s: as conexões ficam na fila.
                int workers, io;
                ThreadPool.GetMinThreads(out workers, out io);
                ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Run(background);
            }
        }

        static bool HasArgument(string[] args, string argument)
        {
            return args.Any(arg => arg.Equals(argument, StringComparison.OrdinalIgnoreCase));
        }

        // A instância aberta pela tarefa é elevada: sem administrador, abrir o mutex dela pode ser negado, o que também diz que existe.
        static bool InstanceRunning()
        {
            try
            {
                Mutex existing;
                if (!Mutex.TryOpenExisting(SingleInstanceMutex, MutexRights.Synchronize, out existing)) return false;
                existing.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }
        }

        // Reinício (como administrador ou após atualizar): espera a instância anterior fechar e liberar o mutex.
        static void WaitForPreviousInstance(string[] args)
        {
            const string prefix = "/waitpid:";
            var waitArgument = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            int processId;
            if (waitArgument != null && int.TryParse(waitArgument.Substring(prefix.Length), out processId))
                try { Process.GetProcessById(processId).WaitForExit(15000); } catch { }
        }

        // Já está aberto (talvez na bandeja): pede para a instância existente mostrar o painel.
        static void ShowExistingInstance()
        {
            EventWaitHandle showRequest;
            if (EventWaitHandle.TryOpenExisting(MainWindow.ShowEventName, out showRequest)) using (showRequest) showRequest.Set();
            else MessageBox.Show("O MultiLink Balancer já está em execução (veja a bandeja do sistema).", "MultiLink Balancer");
        }

        // Separado do Main para que os tipos do WebView2 só sejam carregados depois do AssemblyResolve.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run(bool startedFromTray)
        {
            Application.Run(new MainWindow(startedFromTray));
        }

        // As DLLs do WebView2 vão embutidas no .exe e são extraídas na primeira execução.
        static void PrepareWebView2()
        {
            webView2Folder = EmbeddedFiles.ExtractWebView2();
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
                new AssemblyName(e.Name).Name == Path.GetFileNameWithoutExtension(EmbeddedFiles.WebView2Core)
                    ? Assembly.LoadFrom(Path.Combine(webView2Folder, EmbeddedFiles.WebView2Core)) : null;
            SetLoaderFolder();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetLoaderFolder()
        {
            Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(webView2Folder);
        }
    }
}
