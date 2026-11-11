// A interface web dentro da janela (WebView2). Serve o index.html embutido no .exe, recebe os
// comandos da interface e devolve as respostas e o estado.
// Ao ir para a bandeja, o WebView2 fica escondido e suspenso por alguns minutos (reabrir logo depois é imediato) e
// então é destruído para economizar memória.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using MultiLink.App;
using MultiLink.Common;
using MultiLink.Platform;
using MultiLink.Storage;

namespace MultiLink.Ui
{
    class WebViewHost
    {
        const string Origin = "https://app.multilink";
        const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        // interface simples: renderização por software economiza ~40% de memória (sem processo de GPU).
        // Sem proxy: a interface é servida de dentro do .exe e nunca precisa de internet; seguindo o
        // proxy do Windows, ela mandaria "app.multilink" para o próprio MultiLink resolver.
        const string BrowserArguments = "--no-proxy-server " +
            "--disable-gpu --disable-gpu-compositing --renderer-process-limit=1 " +
            "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,CalculateNativeWinOcclusion";
        const string UiHeaders = "Content-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:";

        readonly MainWindow window;
        readonly AppController app;
        readonly Uri devServerUrl = DevServerUrl(); // npm run dev, com DevTools
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        CoreWebView2Environment environment;
        CoreWebView2Controller controller;
        bool creating;
        // Muda a cada Destroy. Uma criação que ainda esperava algo (o WebView2, a limpeza do perfil) vê que ficou para
        // trás e desiste, sem prender a próxima: fechado o WebView no meio da limpeza do perfil, ela nunca termina.
        int generation;
        const int ProfileClearTimeoutMs = 10000;
        const int KeepAliveMs = 5 * 60 * 1000;      // escondido na bandeja por mais tempo que isso: destrói
        const int CrashReopenIntervalMs = 30 * 1000; // navegador parou: reabre sozinho no máximo uma vez nesse intervalo
        readonly Timer keepAlive = new Timer { Interval = KeepAliveMs };
        DateTime lastCrashReopen = DateTime.MinValue;

        public WebViewHost(MainWindow window, AppController app)
        {
            this.window = window;
            this.app = app;
            keepAlive.Tick += (sender, e) => { keepAlive.Stop(); if (!window.Visible) Destroy(); };
        }

        public bool IsOpen { get { return controller != null; } }

        // Painel aberto: mostra o WebView que ficou escondido ou cria um.
        public void Open()
        {
            keepAlive.Stop();
            if (controller == null) { EnsureCreated(); return; }
            controller.IsVisible = true; // também tira da suspensão
            try { controller.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
            FitToWindow();
            NotifyMoved();
            // a interface volta à tela inicial, como numa abertura nova, e recebe o estado atual de uma vez
            Post(new Dictionary<string, object> { { "type", "panel.opened" } });
            PostState();
        }

        // Painel foi para a bandeja: o WebView fica escondido e suspenso, e é destruído se o painel não reabrir a tempo.
        public void Conceal()
        {
            keepAlive.Stop();
            keepAlive.Start();
            if (controller != null) Suspend();
        }

        async void Suspend()
        {
            try
            {
                controller.IsVisible = false;
                var webView = controller.CoreWebView2;
                webView.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await webView.TrySuspendAsync(); // para os timers e a renderização da página
            }
            catch { } // destruído ou reaberto no meio: nada a fazer
        }

        async void EnsureCreated()
        {
            if (controller != null || creating || !window.Visible) return;
            creating = true;
            int attempt = generation;
            try
            {
                if (environment == null && Elevation.IsAdmin) ClearWebView2Variables();
                if (environment == null)
                    environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Local, "WebView2"),
                                                                           new CoreWebView2EnvironmentOptions(BrowserArguments));
                var created = await environment.CreateCoreWebView2ControllerAsync(window.Handle);
                if (attempt != generation) { created.Close(); return; } // destruído enquanto criava
                controller = created;
                controller.DefaultBackgroundColor = window.BackColor;
                FitToWindow();
                // foi para a bandeja enquanto criava: termina de carregar escondido, pronto para a próxima abertura
                if (!window.Visible) controller.IsVisible = false;
                await Configure(controller.CoreWebView2, attempt);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                window.RunModal(() => MessageBox.Show(window,
                    "O componente Microsoft Edge WebView2 não está instalado.\n\nInstale em: " + WebView2DownloadUrl + "\n\nO monitoramento continua funcionando pela bandeja.",
                    "MultiLink Balancer", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
            catch (Exception ex)
            {
                app.Log.Add("Erro ao abrir a interface: " + ex.Message, ActivityLog.Error);
            }
            finally { if (attempt == generation) creating = false; }
        }

        // O carregador e o runtime do WebView2 leem variáveis WEBVIEW2_* (pasta do navegador, argumentos extras como
        // porta de depuração remota, pasta de dados). Elas vêm do ambiente do usuário, que qualquer programa dele
        // altera: na instância elevada são apagadas do processo antes de criar o WebView2.
        static void ClearWebView2Variables()
        {
            foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                var name = variable.Key as string;
                if (name != null && name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable(name, null);
            }
        }

        async Task Configure(CoreWebView2 webView, int attempt)
        {
            bool devMode = devServerUrl != null;
            bool devTools = devMode || DevBuild.Available; // F12, F5 e o menu de contexto
            var settings = webView.Settings;
            settings.AreDevToolsEnabled = devTools;
            settings.AreDefaultContextMenusEnabled = devTools;
            settings.AreBrowserAcceleratorKeysEnabled = devTools;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsNonClientRegionSupportEnabled = false; // painel fixo junto à bandeja: não arrasta
            webView.WebMessageReceived += OnWebMessage;
            webView.NavigationStarting += (sender, e) => { if (!IsOwnOrigin(e.Uri)) e.Cancel = true; };
            webView.NewWindowRequested += (sender, e) => { e.Handled = true; };
            webView.ProcessFailed += OnProcessFailed;
            if (!devMode)
            {
                webView.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                webView.WebResourceRequested += ServeUi;
            }
            // A pasta de dados do WebView2 fica em %LOCALAPPDATA%, que qualquer programa do usuário altera: um service
            // worker ou um cache gravado lá para https://app.multilink responderia no lugar do ServeUi e falaria com a
            // ponte. Na instância elevada o perfil é limpo antes de abrir a interface (ela não guarda nada no navegador).
            if (Elevation.IsAdmin)
            {
                var clearing = webView.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                if (await Task.WhenAny(clearing, Task.Delay(ProfileClearTimeoutMs)) != clearing)
                {
                    // sem a limpeza a interface não abre (é ela que tira o que outro programa pode ter gravado no perfil)
                    if (attempt != generation) return;
                    Destroy();
                    app.Log.Add("Erro ao abrir a interface: a limpeza do perfil do WebView2 não terminou em " +
                                ProfileClearTimeoutMs / 1000 + " s. Feche e abra o painel de novo.", ActivityLog.Error);
                    return;
                }
                await clearing; // um erro da limpeza vai para o registro, e a interface não abre
                if (attempt != generation) return; // foi para a bandeja enquanto limpava
            }
            webView.Navigate(devMode ? devServerUrl.AbsoluteUri : Origin + "/");
            if (!window.Visible) Suspend(); // carregada escondida (ver EnsureCreated)
        }

        // Com o WebView vivo por mais tempo (escondido na bandeja), o navegador ou a página podem parar no meio. Sem isso
        // o painel ficaria vazio até o app reiniciar.
        void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                Destroy();
                environment = null; // o ambiente morre com o navegador
                bool reopen = window.Visible && (DateTime.UtcNow - lastCrashReopen).TotalMilliseconds > CrashReopenIntervalMs;
                app.Log.Add("O navegador da interface (WebView2) parou" + (reopen ? "; abrindo de novo." : "."), ActivityLog.Error);
                if (reopen) { lastCrashReopen = DateTime.UtcNow; EnsureCreated(); }
            }
            else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                     e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                app.Log.Add("A página da interface parou (" + e.ProcessFailedKind + "); recarregando.", ActivityLog.Error);
                try { ((CoreWebView2)sender).Reload(); } catch { }
            }
        }

        // MULTILINK_DEV_URL troca a origem em que a ponte confia. Vale só no build de desenvolvimento (build.ps1 ao lado
        // do .exe), sem administrador e para um servidor no próprio PC: a variável vem do ambiente do usuário, que
        // qualquer programa dele altera, e a instância elevada passaria a obedecer a página desse programa.
        static Uri DevServerUrl()
        {
            var value = Environment.GetEnvironmentVariable("MULTILINK_DEV_URL");
            Uri url;
            if (string.IsNullOrEmpty(value) || !DevBuild.Available || Elevation.IsAdmin) return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
                !(url.IsLoopback || string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))) return null;
            return url;
        }

        public void Destroy()
        {
            keepAlive.Stop();
            generation++;
            creating = false;
            if (controller == null) return;
            try { controller.Close(); } catch { }
            controller = null;
        }

        public void FitToWindow() { if (controller != null) controller.Bounds = window.ClientRectangle; }

        public void Show() { if (controller != null && window.Visible) controller.IsVisible = true; }

        // Janela do processo do WebView2 (o DevTools, por exemplo).
        public bool OwnsWindow(IntPtr hwnd)
        {
            if (controller == null || hwnd == IntPtr.Zero) return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            try { return processId == controller.CoreWebView2.BrowserProcessId; } catch { return false; }
        }

        public void NotifyMoved() { if (controller != null) controller.NotifyParentWindowPositionChanged(); }

        public void Post(object message)
        {
            if (controller == null) return;
            try { controller.CoreWebView2.PostWebMessageAsJson(json.Serialize(message)); } catch { }
        }

        public void PostState()
        {
            // escondido na bandeja não recebe: ao reabrir, o Open manda o estado atual
            if (controller != null && window.Visible) Post(new Dictionary<string, object> { { "type", "state" }, { "data", app.GetState() } });
        }

        bool IsOwnOrigin(string uri)
        {
            // compara esquema, host e porta (com StartsWith, "http://localhost:5173.exemplo.com" passaria)
            Uri parsed;
            if (uri == null || !Uri.TryCreate(uri, UriKind.Absolute, out parsed)) return false;
            var origin = devServerUrl ?? new Uri(Origin + "/");
            return parsed.Scheme == origin.Scheme && parsed.Port == origin.Port &&
                   string.Equals(parsed.Host, origin.Host, StringComparison.OrdinalIgnoreCase);
        }

        void ServeUi(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var path = new Uri(e.Request.Uri).AbsolutePath;
            e.Response = path == "/" || path == "/index.html"
                ? environment.CreateWebResourceResponse(EmbeddedFiles.Open("ui.index.html"), 200, "OK", UiHeaders)
                : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }

        // Mensagem da interface: { id, cmd, args }. Responde { type: "reply", id, ok, result | error }.
        async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!IsOwnOrigin(e.Source)) return;
            object id = null;
            try
            {
                var message = json.DeserializeObject(e.TryGetWebMessageAsString()) as Dictionary<string, object>;
                if (message == null) return;
                message.TryGetValue("id", out id);
                var command = message.ContainsKey("cmd") ? (string)message["cmd"] : null;
                var args = (message.ContainsKey("args") ? message["args"] as Dictionary<string, object> : null) ?? new Dictionary<string, object>();
                object result;
                switch (command)
                {
                    case "ui.ready": result = new Dictionary<string, object> { { "state", app.GetState() }, { "log", app.Log.Backlog() } }; break;
                    case "window.minimize":
                    case "window.close": window.HidePanel(); result = null; break;
                    case "dev.devTools": controller.CoreWebView2.OpenDevToolsWindow(); result = null; break;
                    case "dev.reload": controller.CoreWebView2.Reload(); result = null; break;
                    default: result = await app.Handle(command, args); break;
                }
                Post(new Dictionary<string, object> { { "type", "reply" }, { "id", id }, { "ok", true }, { "result", result } });
            }
            catch (Exception ex)
            {
                Post(new Dictionary<string, object> { { "type", "reply" }, { "id", id }, { "ok", false }, { "error", ex.Message } });
            }
            PostState(); // estado novo logo após o comando, sem esperar o próximo segundo
        }
    }
}
