// Controlador do app: estado, comandos e eventos, sem nenhuma interface.
// A janela (HostForm) só repassa mensagens da UI web para cá e publica o estado de volta.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiLink
{
    public class AppCore
    {
        public readonly Config Cfg = Config.Load();
        public readonly Balancer Bal = new Balancer();
        public readonly ProxyServer Proxy;
        public readonly LinkMonitor Monitor;
        public readonly History Hist = History.Load();
        public readonly Capture Capture;
        public readonly bool IsAdmin = WinNet.IsAdmin;

        // Ganchos fornecidos pela janela (sempre chamados na thread da UI)
        public Action<string, string, bool> Notify;     // título, mensagem, é alerta?
        public Action<object> Push;                     // mensagem para a UI web
        public Func<string, string> PickSavePath;       // diálogo "Salvar como" nativo
        public Action RequestExit;
        public Action<bool> HotkeysChanged;             // liga/desliga os atalhos globais

        readonly ConcurrentQueue<Dictionary<string, object>> pendingLogs = new ConcurrentQueue<Dictionary<string, object>>();
        readonly List<Dictionary<string, object>> logHistory = new List<Dictionary<string, object>>();
        long logSeq;
        volatile bool netChanged;
        DateTime lastTick = DateTime.UtcNow;
        bool measuring, startup;

        public AppCore()
        {
            Proxy = new ProxyServer(Bal, Log);
            Monitor = new LinkMonitor(Cfg, Log);
            Capture = new Capture(Log);
            Monitor.HealthChanged += OnHealthChanged;
        }

        // ------------------------------------------------------------ ciclo de vida
        public void Init(bool startHidden)
        {
            if (IsAdmin) Capture.KillStale();
            if (Cfg.GetBool("proxyApplied", false))
            {
                SystemIntegration.RestoreProxy(Cfg);
                Log("Configuração de proxy do Windows de uma sessão anterior foi restaurada.");
            }
            if (Cfg.Keys().Any(k => k.StartsWith("metric.")))
            {
                Monitor.RestoreAll();
                Log("Rotas do Windows alteradas numa sessão anterior foram restauradas.");
            }

            Bal.Mode = (BalanceMode)Math.Max(0, Math.Min(3, Cfg.GetInt("mode", 0)));
            Bal.Affinity = Cfg.GetBool("affinity", false);
            Bal.QualityAware = Cfg.GetBool("quality", true);
            Monitor.AutoRoute = Cfg.GetBool("autoRoute", false);
            startup = SystemIntegration.StartupEnabled;
            // como admin: troca o atalho "Run" (sem elevação) pela tarefa agendada elevada
            if (IsAdmin && SystemIntegration.RunKeyStartup) { var ignored = SetStartup(true); }

            LoadKnownAdapters();
            RefreshAdapters();
            var names = Bal.Snapshot().Where(l => l.Present).Select(l => l.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            Log(names.Count > 0 ? "MultiLink aberto com " + string.Join(" e ", names) + "." : "MultiLink aberto. Nenhuma conexão com internet encontrada.");
            NetworkChange.NetworkAddressChanged += (s, e) => netChanged = true;
            NetworkChange.NetworkAvailabilityChanged += (s, e) => netChanged = true;
            Task.Run(() => HealthLoop());

            if (Cfg.GetBool("running", false) || startHidden)
            {
                StartProxy();
                ApplySystemProxyIfPreferred();
            }
        }

        // Primeira execução: mede os links para os pesos refletirem a velocidade real.
        public async void MeasureIfFirstRun()
        {
            if (Cfg.GetBool("measured", false)) return;
            await Task.Delay(1500);
            try { if (!Cfg.GetBool("measured", false)) await Measure(); }
            catch (Exception ex) { Log("Medição automática não concluída: " + ex.Message, "error"); }
        }

        public void Shutdown()
        {
            Cfg.Set("running", Proxy.Running);
            SystemIntegration.RestoreProxy(Cfg);
            Monitor.AutoRoute = false;
            Monitor.RestoreAll(); // nunca deixar o Windows com rotas alteradas depois de fechar
            Capture.Stop(); // antes do proxy: o tun2socks entrega tudo a ele
            Proxy.Stop();
            Hist.Save();
            Cfg.Save();
        }

        // Chamado pela janela a cada 1 s.
        public void Tick()
        {
            if (netChanged) { netChanged = false; RefreshAdapters(); }
            var now = DateTime.UtcNow;
            double dt = Math.Max(0.2, (now - lastTick).TotalSeconds);
            lastTick = now;
            var links = Bal.Snapshot();
            foreach (var l in links)
            {
                long u = Interlocked.Read(ref l.BytesUp), d = Interlocked.Read(ref l.BytesDown);
                l.RateUp = (u - l.LastUp) / dt; l.RateDown = (d - l.LastDown) / dt;
                Hist.Sample(l, d - l.LastDown, u - l.LastUp);
                l.LastUp = u; l.LastDown = d;
            }
            Hist.Flush();
            Monitor.Evaluate(links);
            ReportQuality(links);
            UpdateDownloadRates(dt);

            var fresh = new List<Dictionary<string, object>>();
            Dictionary<string, object> entry;
            while (pendingLogs.TryDequeue(out entry)) fresh.Add(entry);
            if (fresh.Count > 0)
            {
                logHistory.AddRange(fresh);
                if (logHistory.Count > 500) logHistory.RemoveRange(0, logHistory.Count - 500);
                Emit(new Dictionary<string, object> { { "type", "log" }, { "entries", fresh } });
            }
        }

        // Avisa no registro quando um link fica instável (perda alta) e quando melhora. A histerese
        // (10% para entrar, 3% para sair) evita um aviso a cada oscilação.
        void ReportQuality(List<Link> links)
        {
            foreach (var l in links)
            {
                int loss = l.LossPct;
                if (!l.Unstable && loss >= 10)
                {
                    l.Unstable = true;
                    Log(string.Format("[{0}] instável: {1}% dos pacotes se perdendo{2}", l.Name, loss,
                        Bal.QualityAware ? ". Menos conexões vão por ele até melhorar." : "."), "error");
                }
                else if (l.Unstable && (loss < 0 ? !l.Up : loss <= 3))
                {
                    l.Unstable = false;
                    if (l.Up) Log(string.Format("[{0}] estável de novo: {1}% de perda", l.Name, loss), "up");
                }
            }
        }

        public List<string> DownLinks()
        {
            return Bal.Snapshot().Where(x => x.Enabled && x.Health != null && x.Health != LinkMonitor.Ok && !x.UserDisabled).Select(x => x.Name).ToList();
        }

        public double TotalDown() { return Bal.Snapshot().Sum(l => l.RateDown); }
        public double TotalUp() { return Bal.Snapshot().Sum(l => l.RateUp); }

        void Emit(object msg) { var p = Push; if (p != null) p(msg); }

        public void Log(string msg) { Log(msg, "info"); }

        public void Log(string msg, string kind)
        {
            pendingLogs.Enqueue(new Dictionary<string, object>
            {
                { "seq", Interlocked.Increment(ref logSeq) }, { "time", DateTime.Now.ToString("HH:mm:ss") },
                { "kind", kind }, { "text", msg }
            });
        }

        // ------------------------------------------------------------ estado para a UI
        public Dictionary<string, object> GetState()
        {
            // ordem estável por nome: a cor de cada link na UI segue a posição, então não pode mudar entre execuções
            var links = Bal.Snapshot().OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).Select(l =>
            {
                var h = LinkMonitor.HealthOf(l);
                return (object)new Dictionary<string, object>
                {
                    { "id", l.Id }, { "name", l.Name }, { "description", l.Description ?? "" }, { "wired", l.Wired },
                    { "present", l.Present }, { "health", h }, { "healthText", LinkMonitor.ShortText(h).TrimStart('●', '○', ' ') },
                    { "diagnosis", h == LinkMonitor.Ok ? "" : LinkMonitor.Describe(h) },
                    { "enabled", l.Enabled }, { "weight", l.Weight },
                    { "ip", l.Address == null || l.Address.Equals(IPAddress.None) ? "" : l.Address.ToString() },
                    { "gateway", l.Gateway == "—" ? "" : l.Gateway },
                    { "latency", l.LatencyMs }, { "measuredMbps", Math.Round(l.MeasuredMbps) },
                    { "loss", l.LossPct }, { "jitter", l.JitterMs },
                    { "linkSpeed", l.SpeedBps },
                    { "rateDown", Math.Round(l.RateDown) }, { "rateUp", Math.Round(l.RateUp) },
                    { "active", Thread.VolatileRead(ref l.Active) }, { "total", Interlocked.Read(ref l.Total) },
                    { "bytesDown", Interlocked.Read(ref l.BytesDown) }, { "bytesUp", Interlocked.Read(ref l.BytesUp) },
                    { "metricRaised", l.MetricRaised }, { "userDisabled", l.UserDisabled },
                    { "downSince", l.Health != null && l.Health != LinkMonitor.Ok ? (object)ToEpoch(l.DownSince) : null }
                };
            }).ToList();

            return new Dictionary<string, object>
            {
                { "admin", IsAdmin },
                { "proxy", new Dictionary<string, object> {
                    { "running", Proxy.Running }, { "port", Cfg.GetInt("port", 1080) }, { "lan", Cfg.GetBool("lan", false) },
                    { "system", Cfg.GetBool("sysProxy", true) }, { "systemApplied", Cfg.GetBool("proxyApplied", false) } } },
                { "settings", new Dictionary<string, object> {
                    { "mode", (int)Bal.Mode }, { "affinity", Bal.Affinity }, { "quality", Bal.QualityAware }, { "autoRoute", Monitor.AutoRoute }, { "startup", startup },
                    { "notify", Cfg.GetBool("notify", true) }, { "notifySound", Cfg.GetBool("notifySound", true) },
                    { "hotkeys", Cfg.GetBool("hotkeys", true) } } },
                { "capture", new Dictionary<string, object> {
                    { "on", Cfg.GetBool("capture", false) }, { "active", Capture.Active }, { "starting", Capture.Starting }, { "error", Capture.Error },
                    { "ipv6", Capture.Ipv6 } } },
                { "measuring", measuring },
                { "links", links },
                { "download", DownloadState() }
            };
        }

        static long ToEpoch(DateTime t) { return (long)(t.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        public List<Dictionary<string, object>> LogBacklog() { return logHistory.ToList(); }

        // ------------------------------------------------------------ comandos vindos da UI
        public async Task<object> Handle(string cmd, Dictionary<string, object> a)
        {
            switch (cmd)
            {
                case "proxy.start":
                    StartProxy();
                    if (!Proxy.Running) throw new Exception("Não foi possível abrir a porta " + Cfg.GetInt("port", 1080) + ". Outro programa pode estar usando.");
                    ApplySystemProxyIfPreferred();
                    return null;
                case "proxy.stop": StopProxy(); return null;
                case "settings.set": return await SetSetting(Str(a, "key"), a.ContainsKey("value") ? a["value"] : null);
                case "link.set": SetLink(Str(a, "id"), a); return null;
                case "link.toggleAdapter": return await ToggleAdapter(FindLink(Str(a, "id")));
                case "links.test": foreach (var l in Bal.Snapshot()) { var ignored = Net.Check(l, Log); } return null;
                case "adapters.refresh": RefreshAdapters(); return null;
                case "measure": await Measure(); return null;
                case "download.start": await StartDownload(Str(a, "url")); return null;
                case "download.cancel": if (dlCts != null) dlCts.Cancel(); return null;
                case "download.reveal": if (dlPath != null && File.Exists(dlPath)) Process.Start("explorer.exe", "/select,\"" + dlPath + "\""); return null;
                case "download.clear": if (dl == null) dlState = null; return null;
                case "history.get": return Hist.Get(Math.Max(10, Math.Min(1440, a.ContainsKey("minutes") ? Convert.ToInt32(a["minutes"]) : 60)), Bal.Snapshot());
                case "app.restartElevated": RestartElevated(); return null;
                case "app.openNetworkConnections": Process.Start("ncpa.cpl"); return null;
                case "app.exit": var ex = RequestExit; if (ex != null) ex(); return null;
                default: throw new Exception("Comando desconhecido: " + cmd);
            }
        }

        public bool Measuring { get { return measuring; } }

        // Liga/desliga pelo atalho ou pela bandeja. Devolve o novo estado.
        public bool ToggleRunning()
        {
            if (Proxy.Running) StopProxy();
            else { StartProxy(); ApplySystemProxyIfPreferred(); }
            Cfg.Set("running", Proxy.Running); Cfg.Save();
            return Proxy.Running;
        }

        static string Str(Dictionary<string, object> a, string k) { object v; return a != null && a.TryGetValue(k, out v) && v != null ? v.ToString() : null; }

        Link FindLink(string id)
        {
            var l = Bal.Snapshot().FirstOrDefault(x => x.Id == id);
            if (l == null) throw new Exception("Link não encontrado.");
            return l;
        }

        async Task<object> SetSetting(string key, object value)
        {
            switch (key)
            {
                case "port":
                    if (Proxy.Running) throw new Exception("Pare o proxy antes de mudar a porta.");
                    int port = Convert.ToInt32(value);
                    if (port < 1 || port > 65535) throw new Exception("A porta deve estar entre 1 e 65535.");
                    Cfg.Set("port", port);
                    break;
                case "mode": Bal.Mode = (BalanceMode)Math.Max(0, Math.Min(3, Convert.ToInt32(value))); Cfg.Set("mode", (int)Bal.Mode); break;
                case "affinity": Bal.Affinity = Convert.ToBoolean(value); Cfg.Set("affinity", Bal.Affinity); break;
                case "quality": Bal.QualityAware = Convert.ToBoolean(value); Cfg.Set("quality", Bal.QualityAware); break;
                case "lan":
                    Cfg.Set("lan", Convert.ToBoolean(value));
                    if (Proxy.Running) { Proxy.Stop(); StartProxy(); }
                    break;
                case "system":
                    // preferência: com o MultiLink ligado, o Windows passa pelo proxy
                    bool sys = Convert.ToBoolean(value);
                    Cfg.Set("sysProxy", sys);
                    if (Proxy.Running) SetSystemProxy(sys);
                    break;
                case "startup": await SetStartup(Convert.ToBoolean(value)); break;
                case "notify": Cfg.Set("notify", Convert.ToBoolean(value)); break;
                case "notifySound": Cfg.Set("notifySound", Convert.ToBoolean(value)); break;
                case "hotkeys":
                    Cfg.Set("hotkeys", Convert.ToBoolean(value));
                    if (HotkeysChanged != null) HotkeysChanged(Convert.ToBoolean(value));
                    break;
                case "capture":
                    bool cap = Convert.ToBoolean(value);
                    Cfg.Set("capture", cap);
                    Cfg.Save();
                    if (!cap) Capture.Stop();
                    else if (IsAdmin && Proxy.Running) { var ignored = Capture.Start(Cfg.GetInt("port", 1080), HasIpv6); }
                    return new Dictionary<string, object> { { "needsAdmin", cap && !IsAdmin } };
                case "autoRoute":
                    bool on = Convert.ToBoolean(value);
                    Cfg.Set("autoRoute", on);
                    Monitor.AutoRoute = on;
                    if (!on && IsAdmin)
                    {
                        Monitor.RestoreAll();
                        foreach (var l in Bal.Snapshot()) l.MetricRaised = false;
                    }
                    Cfg.Save();
                    return new Dictionary<string, object> { { "needsAdmin", on && !IsAdmin } };
                default: throw new Exception("Ajuste desconhecido: " + key);
            }
            Cfg.Save();
            return null;
        }

        void SetLink(string id, Dictionary<string, object> a)
        {
            var l = FindLink(id);
            if (a.ContainsKey("enabled"))
            {
                l.Enabled = Convert.ToBoolean(a["enabled"]);
                Log(string.Format("[{0}] {1} no balanceamento", l.Name, l.Enabled ? "incluído" : "retirado"));
            }
            if (a.ContainsKey("weight"))
            {
                int w = Convert.ToInt32(a["weight"]);
                if (w < 0 || w > 1000) throw new Exception("O peso deve estar entre 0 e 1000.");
                l.Weight = w;
            }
            Cfg.Set("link." + l.Id, (l.Enabled ? "1" : "0") + ";" + l.Weight);
            Cfg.Save();
        }

        bool HasIpv6() { return Bal.Snapshot().Any(l => l.Present && l.Enabled && l.Address6 != null); }

        // ------------------------------------------------------------ proxy
        void StartProxy()
        {
            if (Proxy.Running) return;
            int port = Cfg.GetInt("port", 1080);
            bool lan = Cfg.GetBool("lan", false);
            try
            {
                Proxy.Start(port, lan);
                Log(string.Format("Proxy iniciado em {0}:{1} (SOCKS4/SOCKS5/HTTP)", lan ? "0.0.0.0" : "127.0.0.1", port));
            }
            catch (Exception ex) { Log("Não foi possível abrir a porta " + port + ": " + ex.Message, "error"); }
        }

        // Ao ligar: proxy do Windows e captura de todos os programas, conforme os ajustes.
        void ApplySystemProxyIfPreferred()
        {
            if (Proxy.Running && Cfg.GetBool("sysProxy", true)) try { SetSystemProxy(true); } catch { }
            if (Proxy.Running && Cfg.GetBool("capture", false))
            {
                if (IsAdmin) { var ignored = Capture.Start(Cfg.GetInt("port", 1080), HasIpv6); }
                else Log("A captura de todos os programas está ligada, mas só funciona com o MultiLink aberto como administrador.", "error");
            }
        }

        void StopProxy()
        {
            Capture.Stop(); // sem o proxy, o adaptador virtual deixaria os programas sem internet
            SetSystemProxy(false); // nunca deixar o Windows apontando para um proxy parado
            Proxy.Stop();
            Log("Proxy parado.");
        }

        void SetSystemProxy(bool on)
        {
            try
            {
                if (on)
                {
                    StartProxy();
                    if (!Proxy.Running) throw new Exception("o proxy não está rodando");
                    SystemIntegration.ApplyProxy(Cfg, Cfg.GetInt("port", 1080));
                    Log("Proxy do Windows apontado para 127.0.0.1:" + Cfg.GetInt("port", 1080));
                }
                else if (Cfg.GetBool("proxyApplied", false))
                {
                    SystemIntegration.RestoreProxy(Cfg);
                    Log("Proxy do Windows restaurado para a configuração anterior.");
                }
            }
            catch (Exception ex) { Log("Erro ao alterar o proxy do Windows: " + ex.Message, "error"); throw; }
        }

        async Task SetStartup(bool on)
        {
            try { await SystemIntegration.SetStartup(on); startup = on; }
            catch (Exception ex) { Log("Erro ao configurar a inicialização com o Windows: " + ex.Message, "error"); throw; }
        }

        void RestartElevated()
        {
            var args = "/waitpid:" + Process.GetCurrentProcess().Id;
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true, Verb = "runas" }); }
            catch (System.ComponentModel.Win32Exception) { throw new Exception("Reinício como administrador cancelado."); }
            var ex = RequestExit;
            if (ex != null) ex();
        }

        // ------------------------------------------------------------ adaptadores
        void ApplySaved(Link f)
        {
            var saved = Cfg.Get("link." + f.Id, null);
            if (saved != null)
            {
                var p = saved.Split(';');
                f.Enabled = p[0] == "1";
                int w; if (p.Length > 1 && int.TryParse(p[1], out w)) f.Weight = w;
            }
            else
            {
                f.Enabled = f.Wired;
                f.Weight = 1;
            }
            f.MeasuredMbps = Cfg.GetInt("speed." + f.Id, -1);
        }

        // Adaptadores desativados no Windows somem da lista do sistema: lembramos deles para poder reativá-los.
        void LoadKnownAdapters()
        {
            Bal.Mutate(links =>
            {
                foreach (var key in Cfg.Keys().Where(k => k.StartsWith("known.")))
                {
                    var p = Cfg.Get(key, "").Split('|');
                    var l = new Link
                    {
                        Id = key.Substring(6), Name = p[0], Description = p.Length > 1 ? p[1] : "", Wired = p.Length > 2 && p[2] == "1",
                        Address = IPAddress.None, Gateway = "—", Present = false, AdapterState = LinkMonitor.Disabled
                    };
                    ApplySaved(l);
                    links.Add(l);
                }
            });
        }

        void RefreshAdapters()
        {
            var found = Net.Discover();
            var all = new Dictionary<string, OperationalStatus>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) all[ni.Id] = ni.OperationalStatus;
            Bal.Mutate(links =>
            {
                var wasPresent = links.ToDictionary(l => l, l => l.Present);
                foreach (var l in links) l.Present = false;
                foreach (var f in found)
                {
                    var ex = links.FirstOrDefault(x => x.Id == f.Id);
                    if (ex == null)
                    {
                        ApplySaved(f);
                        links.Add(f);
                        Log(string.Format("Adaptador detectado: {0} ({1}), IP {2}, gateway {3}", f.Name, f.Description, f.Address, f.Gateway));
                    }
                    else
                    {
                        if (!ex.Address.Equals(f.Address) && !ex.Address.Equals(IPAddress.None)) Log(string.Format("[{0}] IP mudou para {1}", ex.Name, f.Address));
                        ex.Address = f.Address; ex.Address6 = f.Address6; ex.Gateway = f.Gateway; ex.Name = f.Name; ex.SpeedBps = f.SpeedBps;
                        ex.Description = f.Description;
                        if (!wasPresent[ex]) { ex.Up = true; ex.ConsecutiveFailures = 0; }
                        ex.Present = true;
                    }
                    Cfg.Set("known." + f.Id, f.Name + "|" + f.Description + "|" + (f.Wired ? "1" : "0"));
                }
                // por que o adaptador sumiu: cabo, desativado no Windows ou sem IP/gateway
                foreach (var l in links.Where(x => !x.Present))
                {
                    OperationalStatus st;
                    l.AdapterState = !all.TryGetValue(l.Id, out st) ? LinkMonitor.Disabled
                                   : st == OperationalStatus.Up ? LinkMonitor.NoConfig : LinkMonitor.Unplugged;
                }
            });
            Cfg.Save();
            foreach (var l in Bal.Snapshot()) { var ignored = Net.Check(l, Log); }
        }

        async Task<object> ToggleAdapter(Link l)
        {
            bool enable = !l.Present && l.AdapterState == LinkMonitor.Disabled;
            Log(string.Format("[{0}] {1} o adaptador no Windows...", l.Name, enable ? "ativando" : "desativando"));
            if (!enable) l.UserDisabled = true;
            var r = await WinNet.SetAdapterEnabled(l.Name, enable);
            if (!r.Item1)
            {
                if (!enable) l.UserDisabled = false;
                var msg = string.Format("Não foi possível {0} {1}: {2}", enable ? "ativar" : "desativar", l.Name, r.Item2);
                Log(msg, "error");
                throw new Exception(msg);
            }
            Log(string.Format("[{0}] adaptador {1} no Windows", l.Name, enable ? "ativado" : "desativado"));
            netChanged = true;
            if (enable) { await Task.Delay(4000); netChanged = true; } // DHCP leva alguns segundos
            return new Dictionary<string, object> { { "enabled", enable } };
        }

        void OnHealthChanged(Link l, string prev, string now)
        {
            bool wentDown = prev == LinkMonitor.Ok, cameBack = now == LinkMonitor.Ok;
            var healthy = Bal.Snapshot().Where(x => x != l && x.Enabled && x.Health == LinkMonitor.Ok).Select(x => x.Name).ToList();
            bool notify = Cfg.GetBool("notify", true) && Notify != null;
            if (wentDown)
            {
                if (l.UserDisabled) { Log("[" + l.Name + "] desativado por você no Windows"); return; }
                Hist.OutageStart(l, now);
                var msg = l.Name + ": " + LinkMonitor.Describe(now) + ".";
                msg += healthy.Count > 0 ? " Tráfego seguindo por " + string.Join(", ", healthy) + "." : " Nenhum outro link disponível.";
                Log(msg, "down");
                if (l.Enabled && notify) Notify("Link fora do ar: " + l.Name, msg, true);
            }
            else if (cameBack)
            {
                Hist.OutageEnd(l);
                var d = DateTime.Now - l.DownSince;
                var msg = string.Format("{0} voltou a funcionar depois de {1}.", l.Name,
                    d.TotalMinutes >= 1 ? string.Format("{0} min {1} s", (int)d.TotalMinutes, d.Seconds) : string.Format("{0} s", (int)d.TotalSeconds));
                Log(msg, "up");
                if (l.Enabled && prev != LinkMonitor.Disabled && notify) Notify("Link de volta: " + l.Name, msg, false);
            }
            else Log("[" + l.Name + "] " + LinkMonitor.Describe(now));
        }

        async Task HealthLoop()
        {
            while (true)
            {
                var links = Bal.Snapshot();
                await Task.WhenAll(links.Select(l => Net.Check(l, Log)));
                await Task.Delay(3000);
            }
        }

        // ------------------------------------------------------------ medição de velocidade
        async Task Measure()
        {
            var links = Bal.Snapshot().Where(l => l.Present && l.Up).ToList();
            if (links.Count == 0) throw new Exception("Nenhum link online para medir.");
            if (measuring) return;
            measuring = true;
            Log("Medindo a velocidade de " + links.Count + " link(s) em paralelo (cerca de 8 s)...");
            try
            {
                var results = await Task.WhenAll(links.Select(l => Net.MeasureMbps(l, 8)));
                for (int i = 0; i < links.Count; i++)
                {
                    var l = links[i];
                    if (results[i] < 1) { Log(string.Format("[{0}] a medição falhou; peso mantido em {1}", l.Name, l.Weight), "error"); continue; }
                    l.MeasuredMbps = results[i];
                    Cfg.Set("speed." + l.Id, Math.Round(results[i]));
                    // peso proporcional à velocidade: 700 Mbps -> 70, 95 Mbps -> 10
                    l.Weight = Math.Max(1, Math.Min(1000, (int)Math.Round(results[i] / 10)));
                    Cfg.Set("link." + l.Id, (l.Enabled ? "1" : "0") + ";" + l.Weight);
                    Log(string.Format("[{0}] {1:0} Mbps, peso {2}", l.Name, results[i], l.Weight));
                }
                if (results.Any(r => r >= 1)) Cfg.Set("measured", true);
                Cfg.Save();
            }
            finally { measuring = false; }
        }

        // ------------------------------------------------------------ download acelerado
        SegmentedDownload dl;
        CancellationTokenSource dlCts;
        string dlName, dlPath, dlState, dlMessage;
        long dlLastDone, dlTotal = -1, dlDone;
        double dlRate, dlAvgMbps;
        readonly Dictionary<Link, long> dlLastPer = new Dictionary<Link, long>();
        readonly Dictionary<Link, double> dlRatePer = new Dictionary<Link, double>();
        Dictionary<Link, long> dlFinalPer;

        async Task StartDownload(string url)
        {
            if (dl != null) throw new Exception("Já existe um download em andamento.");
            Uri u;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                throw new Exception("Informe um link que comece com http:// ou https://");
            var links = Bal.Snapshot().Where(l => l.Enabled && l.Present && l.Up && l.Weight > 0).ToList();
            if (links.Count == 0) throw new Exception("Nenhum link online e ativo para baixar.");

            dlState = "probing"; dlMessage = "Verificando o arquivo..."; dlName = null; dlPath = null; dlFinalPer = null;
            Tuple<Uri, long, bool> info;
            try { info = await SegmentedDownload.Probe(u.ToString()); }
            catch (Exception ex) { dlState = null; throw new Exception("Não foi possível acessar o link: " + ex.Message); }

            string name = Path.GetFileName(Uri.UnescapeDataString(info.Item1.LocalPath));
            if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var path = PickSavePath == null ? null : PickSavePath(name);
            if (path == null) { dlState = null; return; }

            dl = new SegmentedDownload(Log);
            dlCts = new CancellationTokenSource();
            dlName = Path.GetFileName(path); dlPath = path; dlTotal = info.Item2; dlDone = 0; dlLastDone = 0; dlRate = 0;
            dlLastPer.Clear(); dlRatePer.Clear();
            dlState = "running"; dlMessage = null;
            var sw = Stopwatch.StartNew();
            try
            {
                if (info.Item3)
                {
                    Log(string.Format("Download acelerado: {0} por {1} link(s)", dlName, links.Count));
                    await dl.Run(info.Item1, info.Item2, path, links, dlCts.Token);
                }
                else
                {
                    Log("O servidor não aceita download em partes; baixando por um único link.");
                    dlMessage = "O servidor não aceita download em partes: baixando por um único link.";
                    await dl.RunSingle(info.Item1, path, dlCts.Token);
                }
                dlDone = dl.Done; dlTotal = dl.Total > 0 ? dl.Total : dl.Done;
                dlAvgMbps = dl.Done * 8 / Math.Max(0.1, sw.Elapsed.TotalSeconds) / 1e6;
                dlFinalPer = dl.PerLink.ToDictionary(p => p.Key, p => p.Value);
                dlState = "done";
                Log(string.Format("Download concluído: {0} em {1:0.0} s, média de {2:0} Mbps", dlName, sw.Elapsed.TotalSeconds, dlAvgMbps), "up");
                if (Cfg.GetBool("notify", true) && Notify != null) Notify("Download concluído", string.Format("{0}, média de {1:0} Mbps", dlName, dlAvgMbps), false);
            }
            catch (Exception ex)
            {
                bool canceled = dlCts.IsCancellationRequested;
                dlState = canceled ? "canceled" : "error";
                dlMessage = canceled ? "Download cancelado." : "O download falhou: " + ex.Message;
                Log(dlMessage, canceled ? "info" : "error");
                if (!canceled && Cfg.GetBool("notify", true) && Notify != null) Notify("Download falhou", dlMessage, true);
                try { File.Delete(path); } catch { }
            }
            finally { dl = null; }
        }

        void UpdateDownloadRates(double dt)
        {
            var d = dl;
            if (d == null) return;
            dlDone = Interlocked.Read(ref d.Done);
            if (d.Total > 0) dlTotal = d.Total;
            dlRate = (dlDone - dlLastDone) / dt;
            dlLastDone = dlDone;
            foreach (var p in d.PerLink)
            {
                long last; dlLastPer.TryGetValue(p.Key, out last);
                dlRatePer[p.Key] = (p.Value - last) / dt;
                dlLastPer[p.Key] = p.Value;
            }
        }

        object DownloadState()
        {
            if (dlState == null) return null;
            var per = new List<object>();
            var source = dl != null ? dl.PerLink.ToDictionary(p => p.Key, p => p.Value) : (dlFinalPer ?? new Dictionary<Link, long>());
            foreach (var p in source)
            {
                double r; dlRatePer.TryGetValue(p.Key, out r);
                per.Add(new Dictionary<string, object> { { "id", p.Key.Id }, { "name", p.Key.Name }, { "bytes", p.Value }, { "rate", dl != null ? Math.Round(r) : 0 } });
            }
            return new Dictionary<string, object>
            {
                { "state", dlState }, { "name", dlName }, { "path", dlPath }, { "message", dlMessage },
                { "total", dlTotal }, { "done", dlDone }, { "rate", dl != null ? Math.Round(dlRate) : 0 },
                { "avgMbps", Math.Round(dlAvgMbps) }, { "perLink", per }
            };
        }
    }
}
