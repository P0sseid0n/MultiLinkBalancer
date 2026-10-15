// Janela nativa: hospeda a UI web (WebView2), bandeja do sistema e notificações.
// A janela é um painel da bandeja: não aparece na barra de tarefas, abre ancorada ao ícone
// com um clique e se esconde ao perder o foco.
// O WebView2 só existe enquanto a janela está visível; na bandeja ele é destruído para economizar memória.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MultiLink
{
    public class HostForm : Form
    {
        const string Origin = "https://app.multilink";
        readonly AppCore core = new AppCore();
        readonly bool startHidden;
        readonly string devUrl = Environment.GetEnvironmentVariable("MULTILINK_DEV_URL");
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly NotifyIcon tray;
        readonly Icon iconOk, iconAlert;
        readonly Timer timer;
        CoreWebView2Environment env;
        CoreWebView2Controller ctl;
        bool creating, exiting, alertIcon;
        int modal; // diálogos abertos pela janela: perder o foco para eles não esconde o painel
        DateTime hiddenAt; // o clique no ícone que tirou o foco do painel não deve reabri-lo

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        static bool DarkTheme
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Equals(k.GetValue("AppsUseLightTheme"), 0);
            }
        }

        public HostForm(bool startHidden)
        {
            this.startHidden = startHidden;
            Text = "MultiLink Balancer" + (core.IsAdmin ? " (administrador)" : "");
            iconOk = MakeIcon(AppIcon.On);
            iconAlert = MakeIcon(AppIcon.Alert);
            Icon = iconOk;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; // só a bandeja abre o app
            trayGap = Scale(new Size(12, 12)).Width;
            Size = savedSize = Scale(WindowSize); // sem moldura: o tamanho da janela é o da interface
            MaximizeBox = false; // tamanho fixo: duplo clique no topo não maximiza
            BackColor = Color.FromArgb(0x13, 0x17, 0x1c); // a interface é sempre escura

            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) => { BuildTrayMenu(menu); e.Cancel = false; };
            menu.Items.Add("Abrir MultiLink"); // o menu é montado de novo a cada abertura, com o estado atual
            tray = new NotifyIcon { Icon = iconOk, Text = "MultiLink Balancer", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFromTray(); };
            tray.BalloonTipClicked += (s, e) => ShowFromTray();

            core.Notify = (title, msg, alert) =>
            {
                if (!alert && Visible) return; // com o painel aberto, a própria interface já mostra
                tray.ShowBalloonTip(8000, title, msg, alert ? ToolTipIcon.Warning : ToolTipIcon.Info);
                if (alert && core.Cfg.GetBool("notifySound", true)) System.Media.SystemSounds.Exclamation.Play();
            };
            core.HotkeysChanged = SetHotkeys;
            core.Push = Post;
            core.PickSavePath = name =>
            {
                modal++;
                try
                {
                    using (var dlg = new SaveFileDialog { FileName = name, InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
                }
                finally { modal--; }
            };
            core.RequestExit = ExitApp;
            core.Init(startHidden);

            // segunda execução do .exe: traz esta janela para a frente
            var showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, Program.ShowEventName);
            new System.Threading.Thread(() =>
            {
                while (showEvent.WaitOne())
                    try { BeginInvoke(new Action(() => ShowFromTray(false))); } catch { return; }
            }) { IsBackground = true }.Start();

            timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) => OnTick();
            timer.Start();

            if (!startHidden)
                tray.ShowBalloonTip(4000, "MultiLink está na bandeja", "Clique no ícone para abrir. Para encerrar, use \"Sair\" no menu do ícone.", ToolTipIcon.Info);
        }

        static readonly Size WindowSize = new Size(468, 704);

        Size Scale(Size s)
        {
            using (var g = CreateGraphics()) return new Size((int)(s.Width * g.DpiX / 96), (int)(s.Height * g.DpiY / 96));
        }

        protected override void SetVisibleCore(bool value)
        {
            // sempre começa escondida: a janela só é aberta pelo ícone da bandeja
            if (!IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4); // barra de título escura, como a interface
            int round = 2; // DWMWCP_ROUND: sem área não cliente o Windows 11 não arredonda os cantos sozinho
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4);
            SetHotkeys(core.Cfg.GetBool("hotkeys", true));
            core.MeasureIfFirstRun();
        }

        // O WinForms soma ao tamanho uma moldura que não existe mais (ao carregar, mostrar e restaurar),
        // o que fazia o painel crescer para baixo, para trás da barra de tarefas. Tamanho e posição
        // são impostos em WM_WINDOWPOSCHANGING, então nenhum ajuste posterior os altera.
        Size savedSize;
        FormWindowState lastState = FormWindowState.Normal;

        void RestoreSavedSize()
        {
            if (WindowState != FormWindowState.Normal) return;
            var r = new Rectangle(TrayPosition(savedSize), savedSize);
            if (Bounds != r) Bounds = r;
        }

        // Ancora o painel no canto da área de trabalho junto à barra de tarefas (onde fica a bandeja),
        // alinhado ao cursor, como os painéis do próprio Windows.
        Point anchor;
        Rectangle trayFlyout; // menu de ícones ocultos (^) aberto no clique; vazio se o ícone está na barra
        int trayGap; // distância do painel até a barra de tarefas

        Point TrayPosition(Size size)
        {
            int Width = size.Width, Height = size.Height;
            var screen = Screen.FromPoint(anchor);
            Rectangle wa = screen.WorkingArea, b = screen.Bounds;
            int gap = trayGap;
            int x, y;
            if (wa.Left > b.Left) { x = wa.Left + gap; y = anchor.Y - Height / 2; }           // barra à esquerda
            else if (wa.Right < b.Right) { x = wa.Right - Width - gap; y = anchor.Y - Height / 2; } // à direita
            else
            {
                x = anchor.X - Width / 2;
                y = wa.Top > b.Top ? wa.Top + gap : wa.Bottom - Height - gap; // em cima ou embaixo
            }
            if (!trayFlyout.IsEmpty) // abre acima (ou abaixo, com a barra em cima) do menu de ícones ocultos
                y = wa.Top > b.Top ? trayFlyout.Bottom + gap : trayFlyout.Top - Height - gap;
            x = Math.Max(wa.Left + gap, Math.Min(x, wa.Right - Width - gap));
            y = Math.Max(wa.Top + gap, Math.Min(y, wa.Bottom - Height - gap));
            return new Point(x, y);
        }

        // Clique fora do painel: volta para a bandeja.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (modal == 0 && Visible && !exiting) HideToTray();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { BeginInvoke(new Action(RestoreSavedSize)); EnsureWebView(); }
            else DestroyWebView();
        }

        // ------------------------------------------------------------ janela sem barra de título
        // A área "não cliente" (barra de título e moldura) é removida; o Windows continua desenhando
        // sombra e cantos arredondados. A janela não é redimensionável nem arrastável.
        const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_WINDOWPOSCHANGING = 0x46;
        const int SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy, flags; }

        Rectangle WebBounds { get { return new Rectangle(Point.Empty, ClientSize); } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero; // a janela inteira vira área cliente
                return;
            }
            if (m.Msg == WM_WINDOWPOSCHANGING && WindowState == FormWindowState.Normal)
            {
                // painel fixo: sempre o tamanho da interface, no canto junto à bandeja
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                var p = TrayPosition(savedSize);
                wp.x = p.X; wp.y = p.Y; wp.cx = savedSize.Width; wp.cy = savedSize.Height;
                wp.flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            if (m.Msg == WM_HOTKEY) { OnHotkey(m.WParam.ToInt32()); return; }
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = new IntPtr(1); // HTCLIENT em toda a janela: sem bordas de redimensionamento
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------ atalhos globais
        // Ctrl+Alt+M liga/desliga o MultiLink; Ctrl+Alt+N abre/fecha o painel. Funcionam com qualquer janela em foco.
        const int WM_HOTKEY = 0x312, HK_TOGGLE = 1, HK_PANEL = 2;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        bool hotkeysOn;

        void SetHotkeys(bool on)
        {
            if (hotkeysOn) { UnregisterHotKey(Handle, HK_TOGGLE); UnregisterHotKey(Handle, HK_PANEL); hotkeysOn = false; }
            if (!on) return;
            var busy = new List<string>();
            if (!RegisterHotKey(Handle, HK_TOGGLE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.M)) busy.Add("Ctrl+Alt+M");
            if (!RegisterHotKey(Handle, HK_PANEL, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.N)) busy.Add("Ctrl+Alt+N");
            hotkeysOn = true;
            if (busy.Count > 0) core.Log("Atalho já usado por outro programa: " + string.Join(", ", busy), "error");
        }

        void OnHotkey(int id)
        {
            if (id == HK_PANEL) { if (Visible) HideToTray(); else ShowFromTray(false); return; }
            if (id != HK_TOGGLE) return;
            bool on = core.ToggleRunning();
            // aviso curto: o atalho é usado com o painel fechado
            tray.ShowBalloonTip(2500, on ? "MultiLink ligado" : "MultiLink desligado",
                on ? "Conexões divididas entre os links." : "O Windows voltou à conexão normal.", ToolTipIcon.Info);
        }

        // ------------------------------------------------------------ menu da bandeja
        void BuildTrayMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            bool running = core.Proxy.Running;
            var power = new ToolStripMenuItem(running ? "Desligar MultiLink" : "Ligar MultiLink", null, (s, e) => core.ToggleRunning())
            { ShortcutKeyDisplayString = core.Cfg.GetBool("hotkeys", true) ? "Ctrl+Alt+M" : null };
            power.Font = new Font(power.Font, FontStyle.Bold);
            menu.Items.Add(power);
            menu.Items.Add(new ToolStripMenuItem("Abrir painel", null, (s, e) => ShowFromTray())
                { ShortcutKeyDisplayString = core.Cfg.GetBool("hotkeys", true) ? "Ctrl+Alt+N" : null });
            menu.Items.Add(new ToolStripSeparator());

            var mode = new ToolStripMenuItem("Distribuição");
            string[] modes = { "Proporcional aos pesos", "Menos conexões", "Só um link, com reserva", "Menor latência" };
            for (int i = 0; i < modes.Length; i++)
            {
                int m = i;
                mode.DropDownItems.Add(new ToolStripMenuItem(modes[i], null, (s, e) => Run("settings.set", new Dictionary<string, object> { { "key", "mode" }, { "value", m } }))
                    { Checked = (int)core.Bal.Mode == i });
            }
            menu.Items.Add(mode);

            var links = new ToolStripMenuItem("Links no balanceamento");
            foreach (var l in core.Bal.Snapshot().Where(x => x.Present).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var link = l;
                var h = LinkMonitor.HealthOf(l);
                links.DropDownItems.Add(new ToolStripMenuItem(l.Name + (h == LinkMonitor.Ok ? "" : "  (" + LinkMonitor.ShortText(h).TrimStart('●', '○', ' ') + ")"), null,
                    (s, e) => Run("link.set", new Dictionary<string, object> { { "id", link.Id }, { "enabled", !link.Enabled } }))
                    { Checked = l.Enabled });
            }
            if (links.DropDownItems.Count == 0) links.Enabled = false;
            menu.Items.Add(links);
            menu.Items.Add(new ToolStripMenuItem(core.Measuring ? "Medindo velocidade…" : "Medir velocidade e ajustar pesos", null, (s, e) => Run("measure", null))
                { Enabled = !core.Measuring });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => ExitApp());
        }

        async void Run(string cmd, Dictionary<string, object> args)
        {
            try { await core.Handle(cmd, args ?? new Dictionary<string, object>()); }
            catch (Exception ex) { tray.ShowBalloonTip(5000, "MultiLink", ex.Message, ToolTipIcon.Warning); }
        }

        void HideToTray()
        {
            if (!Visible) return;
            hiddenAt = DateTime.UtcNow;
            Hide();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ctl != null) ctl.Bounds = WebBounds;
            if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; HideToTray(); return; } // sem barra de tarefas, minimizar = esconder
            if (ctl != null) ctl.IsVisible = true;
            if (WindowState != lastState)
            {
                // voltando de minimizada: o WinForms também recalcula o tamanho aqui
                if (lastState == FormWindowState.Minimized) BeginInvoke(new Action(RestoreSavedSize));
                lastState = WindowState;
            }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (ctl != null) ctl.NotifyParentWindowPositionChanged();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // fechar a janela mantém o monitoramento na bandeja
                e.Cancel = true;
                HideToTray();
                return;
            }
            timer.Stop();
            SetHotkeys(false);
            DestroyWebView();
            core.Shutdown();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void ToggleFromTray()
        {
            if (Visible) HideToTray();
            else if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds > 400) ShowFromTray();
        }

        void ShowFromTray() { ShowFromTray(true); }

        void ShowFromTray(bool atCursor)
        {
            // aberto pelo ícone: junto ao cursor; pelo .exe: no canto da bandeja da tela principal
            var wa = Screen.PrimaryScreen.WorkingArea;
            anchor = atCursor ? Cursor.Position : new Point(wa.Right, wa.Bottom);
            trayFlyout = atCursor ? FlyoutUnder(anchor) : Rectangle.Empty;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            RestoreSavedSize();
            Show();
            Activate();
        }

        // Janela sob o cursor no clique: se não for a própria barra de tarefas, é o menu de ícones ocultos.
        Rectangle FlyoutUnder(Point pt)
        {
            var h = GetAncestor(WindowFromPoint(new POINT { X = pt.X, Y = pt.Y }), 2 /* GA_ROOT */);
            if (h == IntPtr.Zero || h == Handle) return Rectangle.Empty;
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString().StartsWith("Shell_", StringComparison.Ordinal)) return Rectangle.Empty; // Shell_TrayWnd, Shell_SecondaryTrayWnd
            RECT r;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf(typeof(RECT))) != 0) return Rectangle.Empty;
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return Screen.FromPoint(pt).WorkingArea.Contains(rect) ? rect : Rectangle.Empty;
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        void OnTick()
        {
            core.Tick();
            var down = core.DownLinks();
            if ((down.Count > 0) != alertIcon)
            {
                alertIcon = down.Count > 0;
                tray.Icon = Icon = alertIcon ? iconAlert : iconOk;
            }
            var tip = down.Count > 0 ? "MultiLink: fora do ar " + string.Join(", ", down)
                                     : "MultiLink: ↓ " + Mbps(core.TotalDown()) + "  ↑ " + Mbps(core.TotalUp());
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (ctl != null) Post(new Dictionary<string, object> { { "type", "state" }, { "data", core.GetState() } });
        }

        static string Mbps(double bytesPerSec) { return (bytesPerSec * 8 / 1e6).ToString("0.0") + " Mbps"; }

        // ------------------------------------------------------------ WebView2
        async void EnsureWebView()
        {
            if (ctl != null || creating || !Visible) return;
            creating = true;
            try
            {
                if (env == null)
                {
                    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiLinkBalancer", "WebView2");
                    // interface simples: renderização por software economiza ~40% de memória (sem processo de GPU)
                    var options = new CoreWebView2EnvironmentOptions(
                        // sem proxy: a interface é servida de dentro do .exe e nunca precisa de internet; seguindo o
                        // proxy do Windows, ela mandaria "app.multilink" para o próprio MultiLink resolver
                        "--no-proxy-server " +
                        "--disable-gpu --disable-gpu-compositing --renderer-process-limit=1 " +
                        "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,CalculateNativeWinOcclusion");
                    env = await CoreWebView2Environment.CreateAsync(null, data, options);
                }
                var c = await env.CreateCoreWebView2ControllerAsync(Handle);
                if (!Visible) { c.Close(); return; } // foi para a bandeja enquanto criava
                ctl = c;
                ctl.DefaultBackgroundColor = BackColor;
                ctl.Bounds = WebBounds;
                var w = ctl.CoreWebView2;
                bool dev = devUrl != null;
                w.Settings.AreDevToolsEnabled = dev;
                w.Settings.AreDefaultContextMenusEnabled = dev;
                w.Settings.AreBrowserAcceleratorKeysEnabled = dev;
                w.Settings.IsStatusBarEnabled = false;
                w.Settings.IsZoomControlEnabled = false;
                w.Settings.IsPasswordAutosaveEnabled = false;
                w.Settings.IsGeneralAutofillEnabled = false;
                w.Settings.IsNonClientRegionSupportEnabled = false; // painel fixo junto à bandeja: não arrasta
                w.WebMessageReceived += OnWebMessage;
                w.NavigationStarting += (s, e) => { if (!IsOwnOrigin(e.Uri)) e.Cancel = true; };
                w.NewWindowRequested += (s, e) => { e.Handled = true; };
                if (!dev)
                {
                    w.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                    w.WebResourceRequested += ServeUi;
                }
                w.Navigate(dev ? devUrl : Origin + "/");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                modal++;
                try { MessageBox.Show(this, "O componente Microsoft Edge WebView2 não está instalado.\n\nInstale em: https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\nO monitoramento continua funcionando pela bandeja.",
                    "MultiLink Balancer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { modal--; }
            }
            catch (Exception ex)
            {
                core.Log("Erro ao abrir a interface: " + ex.Message, "error");
            }
            finally { creating = false; }
        }

        void DestroyWebView()
        {
            if (ctl == null) return;
            try { ctl.Close(); } catch { }
            ctl = null;
        }

        bool IsOwnOrigin(string uri)
        {
            if (uri == null) return false;
            if (devUrl != null) return uri.StartsWith(devUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            return uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase);
        }

        void ServeUi(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var path = new Uri(e.Request.Uri).AbsolutePath;
            if (path != "/" && path != "/index.html")
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.index.html");
            e.Response = env.CreateWebResourceResponse(stream, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:");
        }

        async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!IsOwnOrigin(e.Source)) return;
            object id = null;
            try
            {
                var msg = json.DeserializeObject(e.TryGetWebMessageAsString()) as Dictionary<string, object>;
                if (msg == null) return;
                msg.TryGetValue("id", out id);
                var cmd = msg.ContainsKey("cmd") ? (string)msg["cmd"] : null;
                var args = msg.ContainsKey("args") ? msg["args"] as Dictionary<string, object> : null;
                object result;
                if (cmd == "ui.ready")
                    result = new Dictionary<string, object> { { "state", core.GetState() }, { "log", core.LogBacklog() } };
                else if (cmd == "window.minimize") { HideToTray(); result = null; }
                else if (cmd == "window.close") { HideToTray(); result = null; }
                else
                    result = await core.Handle(cmd, args ?? new Dictionary<string, object>());
                Post(new Dictionary<string, object> { { "type", "reply" }, { "id", id }, { "ok", true }, { "result", result } });
            }
            catch (Exception ex)
            {
                Post(new Dictionary<string, object> { { "type", "reply" }, { "id", id }, { "ok", false }, { "error", ex.Message } });
            }
            // estado novo imediatamente após um comando, sem esperar o próximo segundo
            if (ctl != null) Post(new Dictionary<string, object> { { "type", "state" }, { "data", core.GetState() } });
        }

        void Post(object msg)
        {
            if (ctl == null) return;
            try { ctl.CoreWebView2.PostWebMessageAsJson(json.Serialize(msg)); } catch { }
        }

        // ícone da bandeja no tamanho que o Windows usa nesta tela (16 px a 100%, 24 px a 150%...)
        static Icon MakeIcon(Color accent)
        {
            using (var bmp = AppIcon.Render(SystemInformation.SmallIconSize.Width, accent))
                return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
