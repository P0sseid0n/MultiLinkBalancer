// Ícone da bandeja: abre o painel com um clique, mostra o estado (verde, vermelho ou cinza quando desligado, com a
// velocidade ou os links fora do ar na dica) e tem um menu com os comandos mais usados. Ligando ou desligando, os três
// links acendem um de cada vez (verdes ao ligar, cinza-claro ao desligar) até a transição terminar.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiLink.App;
using MultiLink.Links;
using MultiLink.Platform;

namespace MultiLink.Ui
{
    class TrayIcon
    {
        const int MaxTooltipLength = 63; // limite do Windows para a dica do ícone
        const int FrameMs = 200;         // quadro da animação de ligando/desligando

        static readonly string[] ModeNames = { "Proporcional aos pesos", "Menos conexões", "Só um link, com reserva", "Menor latência" };

        enum Look { On, Alert, Off }

        readonly AppController app;
        readonly NotifyIcon notifyIcon;
        readonly Icon normalIcon, alertIcon, offIcon;
        readonly Icon[] startingFrames, stoppingFrames;
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = FrameMs };
        readonly Action showPanel, exit;
        Look look;
        Icon[] frames; // os quadros que estão passando agora, ou null
        int frame;

        public TrayIcon(AppController app, Action togglePanel, Action showPanel, Action exit)
        {
            this.app = app;
            this.showPanel = showPanel;
            this.exit = exit;
            normalIcon = MakeIcon(AppIcon.On);
            alertIcon = MakeIcon(AppIcon.Alert);
            offIcon = MakeIcon(AppIcon.Off, true);
            startingFrames = BusyFrames(AppIcon.On);
            stoppingFrames = BusyFrames(AppIcon.Stopping);
            look = app.Proxy.Running ? Look.On : Look.Off;
            animation.Tick += (sender, e) => { frame = (frame + 1) % frames.Length; notifyIcon.Icon = frames[frame]; };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir MultiLink"); // o menu é montado de novo a cada abertura, com o estado atual
            menu.Opening += (sender, e) => { BuildMenu(menu); e.Cancel = false; };
            notifyIcon = new NotifyIcon { Icon = CurrentIcon, Text = "MultiLink Balancer", Visible = true, ContextMenuStrip = menu };
            notifyIcon.MouseClick += (sender, e) => { if (e.Button == MouseButtons.Left) togglePanel(); };
            notifyIcon.BalloonTipClicked += (sender, e) => showPanel();
            if (app.Transition != null) Animate(app.Transition); // a partida liga o proxy antes de o ícone existir
        }

        public Icon CurrentIcon { get { return look == Look.Alert ? alertIcon : look == Look.Off ? offIcon : normalIcon; } }

        public void ShowBalloon(int timeoutMs, string title, string text, ToolTipIcon icon)
        {
            notifyIcon.ShowBalloonTip(timeoutMs, title, text, icon);
        }

        // Atualiza ícone e dica. Devolve true se o ícone parado (o da janela) mudou.
        public bool UpdateStatus(List<string> downLinks, double downRate, double upRate)
        {
            var transition = app.Transition;
            // desligado vale mais que o alerta: com o MultiLink parado, um link fora do ar não muda nada
            bool running = app.Proxy.Running;
            var tooltip = transition == AppController.Starting ? "MultiLink: ligando…"
                        : transition == AppController.Stopping ? "MultiLink: desligando…"
                        : !running ? "MultiLink: desligado"
                        : downLinks.Count > 0 ? "MultiLink: fora do ar " + string.Join(", ", downLinks)
                        : "MultiLink: ↓ " + Mbps(downRate) + "  ↑ " + Mbps(upRate);
            notifyIcon.Text = tooltip.Length > MaxTooltipLength ? tooltip.Substring(0, MaxTooltipLength) : tooltip;
            if (transition != null) { Animate(transition); return false; }

            bool wasAnimating = StopAnimation();
            var next = !running ? Look.Off : downLinks.Count > 0 ? Look.Alert : Look.On;
            bool changed = next != look;
            look = next;
            if (changed || wasAnimating) notifyIcon.Icon = CurrentIcon;
            return changed;
        }

        void Animate(string transition)
        {
            var wanted = transition == AppController.Starting ? startingFrames : stoppingFrames;
            if (frames == wanted) return;
            frames = wanted;
            frame = 0;
            notifyIcon.Icon = frames[0];
            animation.Start();
        }

        bool StopAnimation()
        {
            if (frames == null) return false;
            animation.Stop();
            frames = null;
            return true;
        }

        public void Hide() { StopAnimation(); notifyIcon.Visible = false; }

        static string Mbps(double bytesPerSecond) { return (bytesPerSecond * 8 / 1e6).ToString("0.0") + " Mbps"; }

        void BuildMenu(ContextMenuStrip menu)
        {
            bool hotkeys = app.Settings.Hotkeys;
            menu.Items.Clear();

            var transition = app.Transition;
            var powerText = transition == AppController.Starting ? "Ligando…" : transition == AppController.Stopping ? "Desligando…"
                          : app.Proxy.Running ? "Desligar MultiLink" : "Ligar MultiLink";
            var power = new ToolStripMenuItem(powerText, null, (sender, e) => TogglePower())
                { ShortcutKeyDisplayString = hotkeys ? GlobalHotkeys.ToggleShortcut : null, Enabled = transition == null };
            power.Font = new Font(power.Font, FontStyle.Bold);
            menu.Items.Add(power);
            menu.Items.Add(new ToolStripMenuItem("Abrir painel", null, (sender, e) => showPanel())
                { ShortcutKeyDisplayString = hotkeys ? GlobalHotkeys.PanelShortcut : null });
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(ModeMenu());
            menu.Items.Add(LinksMenu());
            bool measuring = app.Links.Measuring;
            bool testingSum = app.Links.TestingSum;
            var paused = app.Links.MeasurePausedUntil; // o servidor de medição bloqueou há pouco
            string pausedText = paused.HasValue ? " (servidor bloqueou, até " + paused.Value.ToString("HH:mm") + ")" : "";
            menu.Items.Add(new ToolStripMenuItem(measuring ? "Medindo velocidade…" : (app.Balancer.UseAutoWeights ? "Medir velocidade" : "Medir velocidade e ajustar pesos") + pausedText, null, (sender, e) => Run("measure", null))
                { Enabled = !measuring && !testingSum && !paused.HasValue });
            menu.Items.Add(new ToolStripMenuItem(testingSum ? "Testando a soma…" : "Testar a soma dos links" + pausedText, null, (sender, e) => TestSum())
                { Enabled = !measuring && !testingSum && !paused.HasValue });
            menu.Items.Add(new ToolStripSeparator());
            if (DevBuild.Available)
            {
                bool building = DevBuild.Running;
                menu.Items.Add(new ToolStripMenuItem(building ? "Recompilando…" : "Recompilar e reiniciar", null, (sender, e) => StartBuild())
                    { Enabled = !building });
            }
            menu.Items.Add("Sair", null, (sender, e) => exit());
        }

        ToolStripMenuItem ModeMenu()
        {
            var modeMenu = new ToolStripMenuItem("Distribuição");
            for (int i = 0; i < ModeNames.Length; i++)
            {
                int mode = i;
                modeMenu.DropDownItems.Add(new ToolStripMenuItem(ModeNames[i], null,
                    (sender, e) => Run("settings.set", new Dictionary<string, object> { { "key", "mode" }, { "value", mode } }))
                    { Checked = (int)app.Balancer.Mode == i });
            }
            return modeMenu;
        }

        ToolStripMenuItem LinksMenu()
        {
            var linksMenu = new ToolStripMenuItem("Links no balanceamento");
            foreach (var link in app.Links.All().Where(candidate => candidate.Present).OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var target = link;
                var health = LinkHealth.Of(link);
                var label = link.Name + (health == LinkHealth.Ok ? "" : "  (" + LinkHealth.ShortText(health) + ")");
                linksMenu.DropDownItems.Add(new ToolStripMenuItem(label, null,
                    (sender, e) => Run("link.set", new Dictionary<string, object> { { "id", target.Id }, { "enabled", !target.Enabled } }))
                    { Checked = link.Enabled });
            }
            if (linksMenu.DropDownItems.Count == 0) linksMenu.Enabled = false;
            return linksMenu;
        }

        async void TogglePower()
        {
            try { await app.ToggleRunning(); }
            catch (Exception ex) { ShowBalloon(5000, "MultiLink", ex.Message, ToolTipIcon.Warning); }
        }

        void StartBuild()
        {
            try { DevBuild.Start(); }
            catch (Exception ex) { ShowBalloon(5000, "MultiLink", ex.Message, ToolTipIcon.Warning); }
        }

        async void Run(string command, Dictionary<string, object> args)
        {
            try { await app.Handle(command, args ?? new Dictionary<string, object>()); }
            catch (Exception ex) { ShowBalloon(5000, "MultiLink", ex.Message, ToolTipIcon.Warning); }
        }

        // O teste leva uns 16 s: o resultado chega num aviso, mesmo com o painel fechado.
        async void TestSum()
        {
            try
            {
                await app.Handle("measure.sum", new Dictionary<string, object>());
                if (app.Links.LastSumTest != null) ShowBalloon(10000, "Teste da soma", app.Links.LastSumTest.Summary(), ToolTipIcon.Info);
            }
            catch (Exception ex) { ShowBalloon(5000, "MultiLink", ex.Message, ToolTipIcon.Warning); }
        }

        // De cima para baixo e volta pelo do meio: 0, 1, 2, 1.
        static Icon[] BusyFrames(Color lit)
        {
            int size = SystemInformation.SmallIconSize.Width;
            var icons = new Icon[4];
            for (int i = 0; i < icons.Length; i++)
                using (var bitmap = AppIcon.RenderBusyFrame(size, lit, i == 3 ? 1 : i))
                    icons[i] = Icon.FromHandle(bitmap.GetHicon());
            return icons;
        }

        // Ícone no tamanho que o Windows usa nesta tela (16 px a 100%, 24 px a 150%...).
        static Icon MakeIcon(Color accent, bool hollowHub = false)
        {
            using (var bitmap = AppIcon.Render(SystemInformation.SmallIconSize.Width, accent, hollowHub))
                return Icon.FromHandle(bitmap.GetHicon());
        }
    }
}
