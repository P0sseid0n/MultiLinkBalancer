// A interface web dentro da janela (WebView2). Serve o index.html embutido no .exe, recebe os
// comandos da interface e devolve as respostas e o estado.
// O WebView2 só existe enquanto a janela está visível; na bandeja ele é destruído para economizar memória.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

        public WebViewHost(MainWindow window, AppController app)
        {
            this.window = window;
            this.app = app;
        }

        public bool IsOpen { get { return controller != null; } }

        public async void EnsureCreated()
        {
            if (controller != null || creating || !window.Visible) return;
            creating = true;
            try
            {
                if (environment == null && Elevation.IsAdmin) ClearWebView2Variables();
                if (environment == null)
                    environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Local, "WebView2"),
                                                                           new CoreWebView2EnvironmentOptions(BrowserArguments));
                var created = await environment.CreateCoreWebView2ControllerAsync(window.Handle);
                if (!window.Visible) { created.Close(); return; } // foi para a bandeja enquanto criava
                controller = created;
                controller.DefaultBackgroundColor = window.BackColor;
                FitToWindow();
                Configure(controller.CoreWebView2);
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
            finally { creating = false; }
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

        void Configure(CoreWebView2 webView)
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
            if (!devMode)
            {
                webView.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                webView.WebResourceRequested += ServeUi;
            }
            webView.Navigate(devMode ? devServerUrl.AbsoluteUri : Origin + "/");
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
            if (controller == null) return;
            try { controller.Close(); } catch { }
            controller = null;
        }

        public void FitToWindow() { if (controller != null) controller.Bounds = window.ClientRectangle; }

        public void Show() { if (controller != null) controller.IsVisible = true; }

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
            if (controller != null) Post(new Dictionary<string, object> { { "type", "state" }, { "data", app.GetState() } });
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
