// Ícone da bandeja: abre o painel com um clique, mostra o estado (verde ou vermelho, com a
// velocidade ou os links fora do ar na dica) e tem um menu com os comandos mais usados.
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

        static readonly string[] ModeNames = { "Proporcional aos pesos", "Menos conexões", "Só um link, com reserva", "Menor latência" };

        readonly AppController app;
        readonly NotifyIcon notifyIcon;
        readonly Icon normalIcon, alertIcon;
        readonly Action showPanel, exit;
        bool alerting;

        public TrayIcon(AppController app, Action togglePanel, Action showPanel, Action exit)
        {
            this.app = app;
            this.showPanel = showPanel;
            this.exit = exit;
            normalIcon = MakeIcon(AppIcon.On);
            alertIcon = MakeIcon(AppIcon.Alert);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir MultiLink"); // o menu é montado de novo a cada abertura, com o estado atual
            menu.Opening += (sender, e) => { BuildMenu(menu); e.Cancel = false; };
            notifyIcon = new NotifyIcon { Icon = normalIcon, Text = "MultiLink Balancer", Visible = true, ContextMenuStrip = menu };
            notifyIcon.MouseClick += (sender, e) => { if (e.Button == MouseButtons.Left) togglePanel(); };
            notifyIcon.BalloonTipClicked += (sender, e) => showPanel();
        }

        public Icon CurrentIcon { get { return alerting ? alertIcon : normalIcon; } }

        public void ShowBalloon(int timeoutMs, string title, string text, ToolTipIcon icon)
        {
            notifyIcon.ShowBalloonTip(timeoutMs, title, text, icon);
        }

        // Atualiza ícone e dica. Devolve true se o ícone mudou.
        public bool UpdateStatus(List<string> downLinks, double downRate, double upRate)
        {
            var tooltip = downLinks.Count > 0 ? "MultiLink: fora do ar " + string.Join(", ", downLinks)
                                              : "MultiLink: ↓ " + Mbps(downRate) + "  ↑ " + Mbps(upRate);
            notifyIcon.Text = tooltip.Length > MaxTooltipLength ? tooltip.Substring(0, MaxTooltipLength) : tooltip;
            bool shouldAlert = downLinks.Count > 0;
            if (shouldAlert == alerting) return false;
            alerting = shouldAlert;
            notifyIcon.Icon = CurrentIcon;
            return true;
        }

        public void Hide() { notifyIcon.Visible = false; }

        static string Mbps(double bytesPerSecond) { return (bytesPerSecond * 8 / 1e6).ToString("0.0") + " Mbps"; }

        void BuildMenu(ContextMenuStrip menu)
        {
            bool hotkeys = app.Settings.Hotkeys;
            menu.Items.Clear();

            var power = new ToolStripMenuItem(app.Proxy.Running ? "Desligar MultiLink" : "Ligar MultiLink", null, (sender, e) => app.ToggleRunning())
                { ShortcutKeyDisplayString = hotkeys ? GlobalHotkeys.ToggleShortcut : null };
            power.Font = new Font(power.Font, FontStyle.Bold);
            menu.Items.Add(power);
            menu.Items.Add(new ToolStripMenuItem("Abrir painel", null, (sender, e) => showPanel())
                { ShortcutKeyDisplayString = hotkeys ? GlobalHotkeys.PanelShortcut : null });
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(ModeMenu());
            menu.Items.Add(LinksMenu());
            bool measuring = app.Links.Measuring;
            bool testingSum = app.Links.TestingSum;
            menu.Items.Add(new ToolStripMenuItem(measuring ? "Medindo velocidade…" : "Medir velocidade e ajustar pesos", null, (sender, e) => Run("measure", null))
                { Enabled = !measuring && !testingSum });
            menu.Items.Add(new ToolStripMenuItem(testingSum ? "Testando a soma…" : "Testar a soma dos links", null, (sender, e) => TestSum())
                { Enabled = !measuring && !testingSum });
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

        // Ícone no tamanho que o Windows usa nesta tela (16 px a 100%, 24 px a 150%...).
        static Icon MakeIcon(Color accent)
        {
            using (var bitmap = AppIcon.Render(SystemInformation.SmallIconSize.Width, accent))
                return Icon.FromHandle(bitmap.GetHicon());
        }
    }
}
