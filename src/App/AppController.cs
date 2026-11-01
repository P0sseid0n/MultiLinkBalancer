// Controlador do app: cria e liga os serviços, roda o ciclo de 1 s e executa os comandos da
// interface (AppController.Commands.cs). Não conhece a janela: fala com ela pelo IAppHost.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Downloads;
using MultiLink.Links;
using MultiLink.Monitoring;
using MultiLink.Platform;
using MultiLink.Proxy;
using MultiLink.Storage;
using MultiLink.Updates;

namespace MultiLink.App
{
    public partial class AppController
    {
        static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(30);
        static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

        public readonly AppSettings Settings = AppSettings.Load();
        public readonly ActivityLog Log = new ActivityLog(System.IO.Path.Combine(AppPaths.Local, "eventos.log"));
        public readonly Balancer Balancer = new Balancer();
        public readonly bool IsAdmin = Elevation.IsAdmin;
        public readonly ProxyServer Proxy;
        public readonly LinkManager Links;
        public readonly TrafficCapture Capture;
        public readonly Updater Updater = new Updater();
        internal readonly LinkMonitor Monitor;
        internal readonly RouteFailover RouteFailover;
        internal readonly UsageHistory History = UsageHistory.Load();
        internal readonly DownloadManager Downloads;
        internal readonly PortUsageMonitor Ports;
        // o "Desativar" do Gerenciador de Tarefas é lido a cada vez: pode mudar com o app aberto
        internal bool StartupEnabled { get { return startupRegistered && !StartupRegistration.DisabledInTaskManager; } }
        bool startupRegistered;

        // suspensão do Windows (ver TrackSleep)
        static readonly TimeSpan SleepTransition = TimeSpan.FromSeconds(30); // a placa de rede desliga até isso antes do PC
        static readonly TimeSpan SuspendWithoutSleep = TimeSpan.FromSeconds(60);
        const long MinSleepMs = 3000;

        readonly IAppHost host;
        readonly DateTime openedAt = DateTime.UtcNow;
        DateTime lastTick = DateTime.UtcNow;
        readonly object powerSync = new object();
        DateTime? suspendSignaledAt; // avisos do Windows, anotados na thread dos eventos e tratados no Tick
        bool resumeSignaled;
        DateTime? asleepSince;
        long sleptMs = SleepClock.SleptMs();

        // desligamento do Windows (ver OnSessionEnding)
        static readonly TimeSpan SessionEndGiveUp = TimeSpan.FromSeconds(60);
        DateTime? proxyReturnedAt; // anotado na thread dos eventos e tratado no Tick

        public AppController(IAppHost host)
        {
            this.host = host;
            Proxy = new ProxyServer(Balancer, Log);
            Links = new LinkManager(Balancer, Settings, Log);
            Capture = new TrafficCapture(Log, Settings);
            Monitor = new LinkMonitor(Log);
            RouteFailover = new RouteFailover(Settings, Log);
            Downloads = new DownloadManager(Log);
            Ports = new PortUsageMonitor(Log);
            Monitor.HealthChanged += OnHealthChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
        }

        // O Windows vai desligar ou fazer logoff: devolve o proxy já, na thread dos eventos. O fechamento pela janela
        // (Shutdown) vem depois e às vezes não termina (o Windows encerra o app que demora, e com a Inicialização
        // Rápida isso aconteceu sem nada no registro de eventos).
        void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
        {
            if (!Settings.SystemProxyApplied) return;
            SystemProxy.Restore(Settings);
            proxyReturnedAt = DateTime.UtcNow;
            Log.Add("Windows " + (e.Reason == Microsoft.Win32.SessionEndReasons.Logoff ? "fazendo logoff" : "desligando") + ": proxy do Windows devolvido.");
            Log.Flush();
        }

        // Um programa pode cancelar o desligamento: com o MultiLink ainda aberto depois disso, o proxy volta.
        void ReapplyProxyIfWindowsStayed()
        {
            var returnedAt = proxyReturnedAt;
            if (returnedAt == null || DateTime.UtcNow - returnedAt.Value < SessionEndGiveUp) return;
            proxyReturnedAt = null;
            if (!Proxy.Running || Settings.SystemProxyApplied) return;
            try { SystemProxy.Apply(Settings, Settings.Port); Log.Add("O Windows não desligou: proxy do Windows apontado de novo para 127.0.0.1:" + Settings.Port); }
            catch (Exception ex) { Log.Add("Erro ao alterar o proxy do Windows: " + ex.Message, ActivityLog.Error); }
        }

        // ------------------------------------------------------------ ciclo de vida
        public void Init(bool startedFromTray)
        {
            var processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Log.Add(string.Format("Iniciando ({0}; processo aberto às {1:HH:mm:ss}, há {2:0.0} s).",
                startedFromTray ? "junto com o Windows" : "aberto por você", processStart, (DateTime.Now - processStart).TotalSeconds));
            RecoverFromPreviousSession();

            Balancer.Mode = (BalanceMode)Math.Max(0, Math.Min(3, Settings.BalanceMode));
            Balancer.Affinity = Settings.Affinity;
            Balancer.QualityAware = Settings.QualityAware;
            Balancer.UseAutoWeights = Settings.AutoWeights;
            Balancer.Ipv6 = (Ipv6Mode)Math.Max(0, Math.Min(2, Settings.Ipv6Mode));
            RouteFailover.Enabled = Settings.AutoRoute;

            Links.Start();
            var names = Links.All().Where(link => link.Present).Select(link => link.Name).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Log.Add(names.Count > 0 ? "MultiLink aberto com " + string.Join(" e ", names) + "." : "MultiLink aberto. Nenhuma conexão com internet encontrada.");

            // Primeiro o proxy: com o proxy do Windows ligado, os programas ficam sem internet até a porta abrir.
            if (Settings.WasRunning || startedFromTray) TurnOn();
            Log.Add(string.Format("Partida concluída {0:0.0} s depois de o processo abrir.", (DateTime.Now - processStart).TotalSeconds));

            // O que não é urgente fica para depois (o schtasks.exe demora na partida do Windows).
            Task.Run(() =>
            {
                startupRegistered = StartupRegistration.IsRegistered;
                // a tarefa das versões anteriores ganha a entrada que aparece no Gerenciador de Tarefas
                if (IsAdmin) StartupRegistration.AddLauncherIfMissing();
            });
            // como administrador: troca o atalho "Run" (sem elevação) pela tarefa agendada elevada
            if (IsAdmin && StartupRegistration.UsesUnelevatedRunKey && !StartupRegistration.DisabledInTaskManager) { var ignored = SetStartup(true); }
            Ports.Start();
            RouteFailover.RepairLeftovers(Links.All());
        }

        // Um fechamento inesperado pode ter deixado o Windows apontando para o proxy ou com rotas alteradas.
        void RecoverFromPreviousSession()
        {
            if (IsAdmin) { TrafficCapture.KillStale(); Capture.RestoreLeftovers(); }
            Updater.Cleanup();
            if (Settings.SystemProxyApplied)
            {
                SystemProxy.Restore(Settings);
                Log.Add("Configuração de proxy do Windows de uma sessão anterior foi restaurada.");
            }
            if (RouteFailover.HasPendingRestore)
            {
                RouteFailover.RestoreAll(Links.All());
                Log.Add("Rotas do Windows alteradas numa sessão anterior foram restauradas.");
            }
        }

        // windowsEnding: o Windows está desligando ou fazendo logoff (e não o "Sair" do menu).
        public void Shutdown(bool windowsEnding)
        {
            Log.Add(windowsEnding ? "Windows desligando: fechando o MultiLink." : "Fechando o MultiLink.");
            Log.Flush(); // no desligamento o Windows pode encerrar o app antes do fim
            Settings.WasRunning = Proxy.Running;
            // antes de tudo: é só o registro, e se o fechamento não terminar os programas ficariam sem internet até o
            // MultiLink abrir de novo
            SystemProxy.Restore(Settings);
            // no desligamento o Windows encerra o tun2socks enquanto o resto fecha, e parado de propósito ele não conta
            // como queda. Antes do proxy: o tun2socks entrega tudo a ele.
            Capture.Stop();
            RouteFailover.Enabled = false;
            RouteFailover.RestoreAll(Links.All()); // nunca deixar o Windows com rotas alteradas depois de fechar
            Proxy.Stop();
            Ports.Stop();
            History.Save();
            Settings.Save();
            Log.Add("MultiLink fechado.");
            Log.Flush();
        }

        // Chamado pela janela a cada 1 s.
        public void Tick()
        {
            TrackSleep();
            ReapplyProxyIfWindowsStayed();
            Links.RefreshIfNetworkChanged();
            Capture.SyncIpv6();
            Capture.SyncLinks();
            var now = DateTime.UtcNow;
            double seconds = Math.Max(0.2, (now - lastTick).TotalSeconds);
            lastTick = now;

            var links = Links.All();
            foreach (var link in links)
            {
                long bytesDown, bytesUp;
                link.Traffic.UpdateRates(seconds, out bytesDown, out bytesUp);
                History.Sample(link, bytesDown, bytesUp);
            }
            Links.UpdateAutoWeights();
            History.Flush();
            Monitor.Evaluate(links, Balancer.QualityAware);
            RouteFailover.Apply(links);
            Downloads.UpdateRates(seconds);
            MemoryTrim.Tick();
            Ports.Tick();

            if (Settings.AutoUpdate && !DevBuild.Available && now - openedAt > FirstUpdateCheckDelay && now - Updater.LastCheck >= UpdateCheckInterval)
            { var ignored = CheckUpdate(); }

            var newEntries = Log.TakeNew();
            if (newEntries.Count > 0) host.Push(new Dictionary<string, object> { { "type", "log" }, { "entries", newEntries } });
        }

        // ------------------------------------------------------------ suspensão
        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            lock (powerSync)
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) suspendSignaledAt = DateTime.UtcNow;
                else if (e.Mode == Microsoft.Win32.PowerModes.Resume) resumeSignaled = true;
            }
        }

        // Ao entrar em suspensão a placa de rede desliga antes do PC, e ao acordar ela volta antes da internet: essas
        // transições não são quedas. Enquanto o PC dorme a internet não importa; se ela tiver caído, o primeiro teste
        // depois de acordar mostra. O aviso do Windows pode chegar tarde ou não chegar (o PC dorme antes do próximo
        // Tick), então o SleepClock também detecta a volta: ele só avança com o PC dormindo.
        void TrackSleep()
        {
            DateTime? suspendAt;
            bool resumed;
            lock (powerSync)
            {
                suspendAt = suspendSignaledAt; suspendSignaledAt = null;
                resumed = resumeSignaled; resumeSignaled = false;
            }
            long slept = SleepClock.SleptMs();
            bool sleptSinceLastTick = slept - sleptMs >= MinSleepMs;
            sleptMs = slept;

            if (asleepSince == null && (suspendAt != null || sleptSinceLastTick))
                GoingToSleep(suspendAt ?? lastTick);
            if (asleepSince != null && (sleptSinceLastTick || resumed || DateTime.UtcNow - asleepSince.Value > SuspendWithoutSleep))
                WokeUp(sleptSinceLastTick || resumed);
        }

        void GoingToSleep(DateTime at)
        {
            asleepSince = at;
            Monitor.Suspend();
            var discarded = History.EndOutagesAtSleep(UnixTime.ToMs(at), (long)SleepTransition.TotalMilliseconds);
            Log.Add("Windows entrando em suspensão." + (discarded.Count == 0 ? "" :
                " Queda descartada (a placa de rede desliga antes do PC): " + string.Join(", ", discarded) + "."));
        }

        void WokeUp(bool slept)
        {
            var duration = DateTime.UtcNow - asleepSince.Value;
            asleepSince = null;
            Monitor.Resume(Links.All());
            Links.CheckAllNow();
            Log.Add(slept ? "Windows voltou da suspensão depois de " + FormatDuration(duration) + "."
                          : "O Windows avisou a suspensão, mas o PC não dormiu.");
        }

        static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalMinutes >= 1 ? string.Format("{0} min {1} s", (int)duration.TotalMinutes, duration.Seconds)
                                              : string.Format("{0} s", (int)duration.TotalSeconds);
        }

        public Dictionary<string, object> GetState() { return UiState.Build(this); }

        public double TotalDownRate() { return Links.All().Sum(link => link.Traffic.RateDown); }
        public double TotalUpRate() { return Links.All().Sum(link => link.Traffic.RateUp); }

        // ------------------------------------------------------------ ligar e desligar
        // Pelo atalho ou pela bandeja. Devolve o novo estado.
        public bool ToggleRunning()
        {
            if (Proxy.Running) TurnOff();
            else TurnOn();
            Settings.WasRunning = Proxy.Running;
            Settings.Save();
            return Proxy.Running;
        }

        // Proxy local e, conforme os ajustes, proxy do Windows e captura de todos os programas.
        void TurnOn()
        {
            StartProxy();
            if (!Proxy.Running) return;
            if (Settings.UseSystemProxy) try { SetSystemProxy(true); } catch { }
            if (Settings.CaptureAllPrograms)
            {
                if (IsAdmin) StartCapture();
                else Log.Add("A captura de todos os programas está ligada, mas só funciona com o MultiLink aberto como administrador.", ActivityLog.Error);
            }
        }

        void TurnOff()
        {
            Capture.Stop(); // sem o proxy, o adaptador virtual deixaria os programas sem internet
            SetSystemProxy(false); // nunca deixar o Windows apontando para um proxy parado
            Proxy.Stop();
            Log.Add("Proxy parado.");
        }

        void StartProxy()
        {
            if (Proxy.Running) return;
            int port = Settings.Port;
            bool allowLan = Settings.AllowLan;
            try
            {
                Proxy.Start(port, allowLan);
                Log.Add(string.Format("Proxy iniciado em {0}:{1} (SOCKS4/SOCKS5/HTTP)", allowLan ? "0.0.0.0" : "127.0.0.1", port));
            }
            catch (Exception ex) { Log.Add("Não foi possível abrir a porta " + port + ": " + ex.Message, ActivityLog.Error); }
        }

        void StartCapture() { var ignored = Capture.Start(Settings.Port, Links.AnyHasIpv6, Links.InterfaceIndexes, Balancer.Ipv6Allowed); }

        void SetSystemProxy(bool enabled)
        {
            proxyReturnedAt = null; // a escolha de agora vale mais que a volta depois de um desligamento cancelado
            try
            {
                if (enabled)
                {
                    StartProxy();
                    if (!Proxy.Running) throw new Exception("o proxy não está rodando");
                    SystemProxy.Apply(Settings, Settings.Port);
                    Log.Add("Proxy do Windows apontado para 127.0.0.1:" + Settings.Port);
                }
                else if (Settings.SystemProxyApplied)
                {
                    SystemProxy.Restore(Settings);
                    Log.Add("Proxy do Windows restaurado para a configuração anterior.");
                }
            }
            catch (Exception ex) { Log.Add("Erro ao alterar o proxy do Windows: " + ex.Message, ActivityLog.Error); throw; }
        }

        async Task SetStartup(bool enabled)
        {
            try { await StartupRegistration.Set(enabled); startupRegistered = enabled; }
            catch (Exception ex) { Log.Add("Erro ao configurar a inicialização com o Windows: " + ex.Message, ActivityLog.Error); throw; }
        }

        // ------------------------------------------------------------ avisos
        void NotifyIfEnabled(string title, string message, bool alert)
        {
            if (Settings.Notify) host.Notify(title, message, alert);
        }

        void OnHealthChanged(Link link, string previous, string current)
        {
            bool wentDown = previous == LinkHealth.Ok, cameBack = current == LinkHealth.Ok;
            if (wentDown) OnLinkDown(link, current);
            else if (cameBack) OnLinkBack(link, previous);
            else Log.Add("[" + link.Name + "] " + LinkHealth.Describe(current));
        }

        void OnLinkDown(Link link, string health)
        {
            if (link.UserDisabled) { Log.Add("[" + link.Name + "] desativado por você no Windows"); return; }
            History.OutageStart(link, health);
            var healthy = Links.All().Where(other => other != link && other.Enabled && other.Health == LinkHealth.Ok).Select(other => other.Name).ToList();
            var message = link.Name + ": " + LinkHealth.Describe(health) + "." +
                          (healthy.Count > 0 ? " Tráfego seguindo por " + string.Join(", ", healthy) + "." : " Nenhum outro link disponível.");
            Log.Add(message, ActivityLog.LinkDown);
            if (link.Enabled) NotifyIfEnabled("Link fora do ar: " + link.Name, message, true);
        }

        void OnLinkBack(Link link, string previous)
        {
            History.OutageEnd(link);
            var downtime = DateTime.Now - link.DownSince;
            var message = string.Format("{0} voltou a funcionar depois de {1}.", link.Name, FormatDuration(downtime));
            Log.Add(message, ActivityLog.LinkUp);
            if (link.Enabled && previous != LinkHealth.Disabled) NotifyIfEnabled("Link de volta: " + link.Name, message, false);
        }

        // ------------------------------------------------------------ atualização
        async Task CheckUpdate()
        {
            if (!await Updater.Check()) return;
            if (Settings.UpdateNotified == Updater.Latest) return; // avisa uma vez por versão
            Settings.UpdateNotified = Updater.Latest;
            Settings.Save();
            var message = "Versão " + Updater.Latest + " disponível (você tem a " + Updater.Current + "). Atualize em Ajustes.";
            Log.Add(message, ActivityLog.LinkUp);
            host.Notify("Atualização do MultiLink", message, false);
        }
    }
}
