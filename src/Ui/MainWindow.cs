// Janela nativa do app. É um painel da bandeja: não aparece na barra de tarefas, abre ancorada ao
// ícone com um clique e se esconde ao perder o foco. Sem barra de título: a janela inteira é a
// interface web (WebViewHost); o Windows continua desenhando sombra e cantos arredondados.
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MultiLink.App;
using MultiLink.Common;

namespace MultiLink.Ui
{
    public class MainWindow : Form, IAppHost
    {
        public const string ShowEventName = "Local\\MultiLinkBalancer.Show"; // segunda execução do .exe pede para mostrar o painel
        static readonly Size PanelSize = new Size(468, 704);                  // em pixels a 100% de escala
        static readonly Color Background = Color.FromArgb(0x13, 0x17, 0x1c);  // a interface é sempre escura
        const int TrayClickDebounceMs = 400;

        readonly AppController app;
        readonly TrayIcon tray;
        readonly WebViewHost webView;
        readonly PanelPlacement placement = new PanelPlacement(); // antes de tudo: o Windows pede a posição já ao criar a janela
        readonly System.Windows.Forms.Timer ticker;
        readonly Size scaledPanelSize;
        GlobalHotkeys hotkeys;
        bool exiting;
        int openDialogs;          // diálogos abertos pela janela: perder o foco para eles não esconde o painel
        DateTime hiddenAt;        // o clique no ícone que tirou o foco do painel não deve reabri-lo
        FormWindowState lastWindowState = FormWindowState.Normal;

        public MainWindow(bool startedFromTray)
        {
            app = new AppController(this);
            webView = new WebViewHost(this, app); // antes de definir o tamanho: OnResize já o usa
            Text = "MultiLink Balancer" + (app.IsAdmin ? " (administrador)" : "");
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; // só a bandeja abre o app
            MaximizeBox = false;   // tamanho fixo: duplo clique no topo não maximiza
            BackColor = Background;
            placement.Gap = ScaleToDpi(new Size(12, 12)).Width;
            scaledPanelSize = ScaleToDpi(PanelSize);
            Size = scaledPanelSize; // sem moldura: o tamanho da janela é o da interface

            tray = new TrayIcon(app, TogglePanelFromTray, () => ShowPanel(true), Exit);
            Icon = tray.CurrentIcon;
            app.Init(startedFromTray);
            ListenForSecondInstance();

            ticker = new System.Windows.Forms.Timer { Interval = 1000 };
            ticker.Tick += (sender, e) => OnTick();
            ticker.Start();

            if (!startedFromTray)
                tray.ShowBalloon(4000, "MultiLink está na bandeja", "Clique no ícone para abrir. Para encerrar, use \"Sair\" no menu do ícone.", ToolTipIcon.Info);
        }

        Size ScaleToDpi(Size size)
        {
            using (var graphics = CreateGraphics())
                return new Size((int)(size.Width * graphics.DpiX / 96), (int)(size.Height * graphics.DpiY / 96));
        }

        // Segunda execução do .exe: traz o painel para a frente.
        void ListenForSecondInstance()
        {
            var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (showRequested.WaitOne())
                    try { BeginInvoke(new Action(() => ShowPanel(false))); } catch { return; }
            }) { IsBackground = true }.Start();
        }

        void OnTick()
        {
            app.Tick();
            if (tray.UpdateStatus(app.Links.DownLinkNames(), app.TotalDownRate(), app.TotalUpRate())) Icon = tray.CurrentIcon;
            webView.PostState();
        }

        // ------------------------------------------------------------ IAppHost
        void IAppHost.Notify(string title, string message, bool alert)
        {
            if (!alert && Visible) return; // com o painel aberto, a própria interface já mostra
            tray.ShowBalloon(8000, title, message, alert ? ToolTipIcon.Warning : ToolTipIcon.Info);
            if (alert && app.Settings.NotifySound) System.Media.SystemSounds.Exclamation.Play();
        }

        void IAppHost.Push(object message) { webView.Post(message); }

        string IAppHost.PickSavePath(string suggestedName)
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return RunModal(() =>
            {
                using (var dialog = new SaveFileDialog { FileName = suggestedName, InitialDirectory = downloads })
                    return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            });
        }

        void IAppHost.SetHotkeys(bool enabled) { SetHotkeys(enabled); }

        void IAppHost.Exit() { Exit(); }

        // Diálogo aberto pela janela: enquanto ele estiver na frente, o painel não se esconde.
        public T RunModal<T>(Func<T> showDialog)
        {
            openDialogs++;
            try { return showDialog(); }
            finally { openDialogs--; }
        }

        void SetHotkeys(bool enabled)
        {
            if (hotkeys == null) return;
            var busy = hotkeys.Set(enabled);
            if (busy.Count > 0) app.Log.Add("Atalho já usado por outro programa: " + string.Join(", ", busy), ActivityLog.Error);
        }

        void OnTogglePressed()
        {
            bool on = app.ToggleRunning();
            // aviso curto: o atalho é usado com o painel fechado
            tray.ShowBalloon(2500, on ? "MultiLink ligado" : "MultiLink desligado",
                on ? "Conexões divididas entre os links." : "O Windows voltou à conexão normal.", ToolTipIcon.Info);
        }

        // ------------------------------------------------------------ mostrar e esconder
        void TogglePanelFromTray()
        {
            if (Visible) HidePanel();
            else if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds > TrayClickDebounceMs) ShowPanel(true);
        }

        // atCursor: aberto pelo ícone (junto ao cursor) ou pelo .exe/atalho (no canto da bandeja).
        void ShowPanel(bool atCursor)
        {
            if (atCursor) placement.AnchorAtCursor(Handle);
            else placement.AnchorAtPrimaryTray();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ApplyPanelBounds();
            Show();
            Activate();
        }

        public void HidePanel()
        {
            if (!Visible) return;
            hiddenAt = DateTime.UtcNow;
            Hide();
        }

        void Exit()
        {
            exiting = true;
            Close();
        }

        // O WinForms soma ao tamanho uma moldura que não existe mais (ao carregar, mostrar e restaurar),
        // o que fazia o painel crescer para baixo, para trás da barra de tarefas. Tamanho e posição
        // também são impostos em WM_WINDOWPOSCHANGING, então nenhum ajuste posterior os altera.
        void ApplyPanelBounds()
        {
            if (WindowState != FormWindowState.Normal) return;
            var bounds = new Rectangle(placement.Position(scaledPanelSize), scaledPanelSize);
            if (Bounds != bounds) Bounds = bounds;
        }

        // ------------------------------------------------------------ eventos da janela
        protected override void SetVisibleCore(bool value)
        {
            // sempre começa escondida: a janela só é aberta pelo ícone da bandeja
            if (!IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int enabled = 1;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, 4);
            // sem área não cliente o Windows 11 não arredonda os cantos sozinho
            int corners = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, 4);
            hotkeys = new GlobalHotkeys(Handle);
            hotkeys.TogglePressed += OnTogglePressed;
            hotkeys.PanelPressed += () => { if (Visible) HidePanel(); else ShowPanel(false); };
            SetHotkeys(app.Settings.Hotkeys);
            app.Links.MeasureOnFirstRun();
        }

        // Clique fora do painel: volta para a bandeja.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (openDialogs == 0 && Visible && !exiting) HidePanel();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { BeginInvoke(new Action(ApplyPanelBounds)); webView.EnsureCreated(); }
            else webView.Destroy();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            webView.FitToWindow();
            // sem barra de tarefas, minimizar = esconder
            if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; HidePanel(); return; }
            webView.Show();
            if (WindowState != lastWindowState)
            {
                // voltando de minimizada: o WinForms também recalcula o tamanho aqui
                if (lastWindowState == FormWindowState.Minimized) BeginInvoke(new Action(ApplyPanelBounds));
                lastWindowState = WindowState;
            }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            webView.NotifyMoved();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // fechar a janela mantém o monitoramento na bandeja
                e.Cancel = true;
                HidePanel();
                return;
            }
            ticker.Stop();
            SetHotkeys(false);
            webView.Destroy();
            app.Shutdown();
            tray.Hide();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case NativeMethods.WM_NCCALCSIZE:
                    if (m.WParam == IntPtr.Zero) break;
                    m.Result = IntPtr.Zero; // a janela inteira vira área cliente
                    return;
                case NativeMethods.WM_NCHITTEST:
                    m.Result = new IntPtr(NativeMethods.HTCLIENT); // sem bordas de redimensionamento
                    return;
                case NativeMethods.WM_HOTKEY:
                    if (hotkeys != null) hotkeys.OnHotkeyMessage(m.WParam.ToInt32());
                    return;
                case NativeMethods.WM_WINDOWPOSCHANGING:
                    if (WindowState == FormWindowState.Normal) KeepPanelBounds(m.LParam);
                    break;
            }
            base.WndProc(ref m);
        }

        // Painel fixo: sempre o tamanho da interface, no canto junto à bandeja.
        void KeepPanelBounds(IntPtr windowPosPointer)
        {
            var windowPos = (NativeMethods.WINDOWPOS)Marshal.PtrToStructure(windowPosPointer, typeof(NativeMethods.WINDOWPOS));
            var position = placement.Position(scaledPanelSize);
            windowPos.x = position.X;
            windowPos.y = position.Y;
            windowPos.cx = scaledPanelSize.Width;
            windowPos.cy = scaledPanelSize.Height;
            windowPos.flags &= ~(NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE);
            Marshal.StructureToPtr(windowPos, windowPosPointer, false);
        }
    }
}
