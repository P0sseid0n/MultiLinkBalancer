// Captura de todo o tráfego IPv4 e IPv6 (inclusive programas que ignoram o proxy do Windows).
// O tun2socks cria um adaptador virtual Wintun ("MultiLink") e entrega cada conexão TCP/UDP
// que chega nele ao proxy SOCKS5 local do MultiLink. Duas rotas /1 (mais específicas que a rota
// padrão) mandam a internet para esse adaptador; as conexões do próprio MultiLink saem presas ao
// IP de cada link, então continuam indo direto pelos adaptadores reais (modelo "strong host").
// A rede local continua direta porque as rotas das sub-redes locais são mais específicas que /1.
// IPv6 (só enquanto algum link tem IPv6 que funciona; ver SyncIpv6): a rota 2000::/3 cobre toda a internet IPv6 e é mais
// específica que a padrão (::/0). O adaptador recebe um endereço ULA; com ele o Windows prefere
// IPv4 nos destinos que têm os dois, e os destinos só IPv6 seguem pelo MultiLink.
//
// Segurança: o tun2socks roda num JobObject. Se o MultiLink fechar ou travar, o Windows encerra
// o tun2socks, o adaptador some e as rotas somem junto com ele.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MultiLink.Common;

namespace MultiLink.Platform
{
    public class TrafficCapture
    {
        public const string AdapterName = "MultiLink";
        // Sem GUID pedido, o Wintun sorteia um a cada criação e o Windows vê uma rede nova a cada vez
        // (perfis "MultiLink 2", "MultiLink 3"...). Fixo, o adaptador volta sempre com o mesmo perfil.
        const string AdapterGuid = "{4AF01592-B47E-47EA-94E3-D17BFBE4B92E}";
        const string TunAddress = "198.18.0.1";      // faixa reservada para testes (RFC 2544): não colide com redes reais
        const string TunAddressV6 = "fd4d:4c42::1";  // ULA (rede privada IPv6, RFC 4193)
        const string ExeName = "multilink-tun2socks.exe";
        const int MaxRestarts = 3;

        readonly ActivityLog log;
        readonly JobObject job = new JobObject();
        Process process;
        volatile bool wanted;
        Func<bool> anyLinkHasIpv6;
        int restarts;
        bool syncingIpv6;
        bool ipv6Failed; // o netsh recusou: não insiste até a captura ser religada
        DateTime restartWindowStart;

        public TrafficCapture(ActivityLog log) { this.log = log; }

        public bool Active { get; private set; }
        public bool Starting { get; private set; }
        public string Error { get; private set; }
        public bool Ipv6 { get; private set; } // o tráfego IPv6 também está sendo capturado

        // Encerra um tun2socks que tenha sobrado de uma execução anterior (não deveria, por causa do JobObject).
        public static void KillStale()
        {
            foreach (var stale in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
                try { stale.Kill(); stale.WaitForExit(3000); } catch { }
        }

        // anyLinkHasIpv6: sem um link com IPv6 funcionando, rotear IPv6 para o adaptador deixaria esses destinos sem saída.
        public async Task Start(int socksPort, Func<bool> anyLinkHasIpv6)
        {
            if (wanted) return;
            this.anyLinkHasIpv6 = anyLinkHasIpv6;
            wanted = true;
            Error = null;
            restarts = 0;
            ipv6Failed = false;
            await Launch(socksPort);
        }

        public void Stop()
        {
            wanted = false;
            Active = false;
            Starting = false;
            Ipv6 = false;
            var running = process;
            process = null;
            if (running == null) return;
            try { if (!running.HasExited) { running.Kill(); running.WaitForExit(5000); } } catch { }
            log.Add("Captura de todos os programas desligada. O adaptador virtual foi removido.");
        }

        async Task Launch(int socksPort)
        {
            Starting = true;
            try
            {
                if (!Elevation.IsAdmin) throw new Exception("requer o MultiLink aberto como administrador");
                var started = StartTun2Socks(socksPort);
                await WaitForAdapter(started);
                await ConfigureIpv4();
                Ipv6 = anyLinkHasIpv6 != null && anyLinkHasIpv6() && await TryConfigureIpv6();

                if (!wanted || started.HasExited) return;
                Active = true;
                log.Add("Captura ligada" + (Ipv6 ? " (IPv4 e IPv6)" : "") + ": todos os programas (inclusive jogos e apps que ignoram o proxy) passam pelo MultiLink.");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                log.Add("Não foi possível capturar todos os programas: " + ex.Message, ActivityLog.Error);
                wanted = false;
                var failed = process;
                process = null;
                try { if (failed != null && !failed.HasExited) failed.Kill(); } catch { }
            }
            finally { Starting = false; }
        }

        Process StartTun2Socks(int socksPort)
        {
            var folder = EmbeddedFiles.ExtractTun2Socks(ExeName);
            KillStale();
            var startInfo = new ProcessStartInfo(Path.Combine(folder, ExeName),
                "--device tun://" + AdapterName + "?guid=" + AdapterGuid + " --proxy socks5://127.0.0.1:" + socksPort + " --loglevel warn --udp-timeout 60s")
            {
                WorkingDirectory = folder, // o wintun.dll é carregado da mesma pasta
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // Sem limite, o Go guarda a memória de um pico de conexões por muito tempo (centenas de MB parados).
            // Limite flexível: acima dele o coletor roda mais vezes e devolve a sobra ao Windows.
            startInfo.EnvironmentVariables["GOMEMLIMIT"] = "256MiB";
            var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            started.OutputDataReceived += (sender, e) => RelayOutput(e.Data);
            started.ErrorDataReceived += (sender, e) => RelayOutput(e.Data);
            started.Exited += (sender, e) => OnExited(started, socksPort);
            started.Start();
            if (!job.Attach(started))
                log.Add("Aviso: não foi possível vincular o tun2socks ao MultiLink; se o app travar, encerre o processo " + ExeName + " manualmente.", ActivityLog.Error);
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            process = started;
            return started;
        }

        static async Task WaitForAdapter(Process tun2socks)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (tun2socks.HasExited) throw new Exception("o tun2socks fechou ao iniciar (código " + tun2socks.ExitCode + ")");
                await Task.Delay(250);
                if (NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.Name == AdapterName)) return;
            }
            throw new Exception("o adaptador virtual não apareceu");
        }

        // Endereço, DNS e rotas (store=active: nada fica salvo no Windows).
        static async Task ConfigureIpv4()
        {
            var adapter = "\"" + AdapterName + "\"";
            await Netsh("interface ipv4 set address name=" + adapter + " source=static address=" + TunAddress + " mask=255.255.255.0");
            await Netsh("interface ipv4 set dnsservers name=" + adapter + " source=static address=1.1.1.1 register=none validate=no");
            await Netsh("interface ipv4 add dnsservers name=" + adapter + " address=8.8.8.8 index=2 validate=no");
            await Netsh("interface ipv4 set interface interface=" + adapter + " metric=1");
            await Netsh("interface ipv4 add route prefix=0.0.0.0/1 interface=" + adapter + " nexthop=" + TunAddress + " metric=1 store=active");
            await Netsh("interface ipv4 add route prefix=128.0.0.0/1 interface=" + adapter + " nexthop=" + TunAddress + " metric=1 store=active");
        }

        // Falhar aqui não derruba a captura IPv4: o IPv6 só continua direto, como antes.
        async Task<bool> TryConfigureIpv6()
        {
            var adapter = "\"" + AdapterName + "\"";
            try
            {
                await Netsh("interface ipv6 add address interface=" + adapter + " address=" + TunAddressV6 + " store=active");
                await Netsh("interface ipv6 set interface interface=" + adapter + " metric=1");
                await Netsh("interface ipv6 add route prefix=2000::/3 interface=" + adapter + " metric=1 store=active");
                return true;
            }
            catch (Exception ex)
            {
                ipv6Failed = true;
                log.Add("A captura vale só para IPv4; o IPv6 segue direto (" + ex.Message + ").", ActivityLog.Error);
                return false;
            }
        }

        // Chamado a cada segundo: o teste de IPv6 dos links termina depois de a captura ligar, e pode mudar
        // com o tempo. Liga ou desliga a rota IPv6 para acompanhar.
        public async void SyncIpv6()
        {
            if (!Active || syncingIpv6 || ipv6Failed || anyLinkHasIpv6 == null) return;
            bool wantIpv6 = anyLinkHasIpv6();
            if (wantIpv6 == Ipv6) return;
            syncingIpv6 = true;
            try
            {
                if (wantIpv6) Ipv6 = await TryConfigureIpv6();
                else Ipv6 = !await TryRemoveIpv6Route();
                if (Active && Ipv6 == wantIpv6)
                    log.Add(wantIpv6 ? "A captura passou a incluir o IPv6." : "Nenhum link com IPv6 funcionando: o IPv6 deixou de ser capturado.");
            }
            finally { syncingIpv6 = false; }
        }

        async Task<bool> TryRemoveIpv6Route()
        {
            try { await Netsh("interface ipv6 delete route prefix=2000::/3 interface=\"" + AdapterName + "\" store=active"); return true; }
            catch (Exception ex) { ipv6Failed = true; log.Add("Não foi possível tirar o IPv6 da captura: " + ex.Message, ActivityLog.Error); return false; }
        }

        // O tun2socks escreve o log em JSON: {"level":"warn","ts":...,"caller":"...","msg":"[TCP] dial ..."}.
        // Ele registra cada conexão que falhou (avisos de rotina): só repassa o que não é rotina, com a mensagem limpa.
        void RelayOutput(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            string level, message;
            ParseLogLine(line.Trim(), out level, out message);
            bool routine = level == "warn" && (message.Contains("dial") || message.Contains("i/o timeout"));
            if (routine || level == "debug") return;
            bool failure = level == "error" || level == "fatal" || level == "panic" || level == "dpanic";
            log.Add("[captura] " + message, failure ? ActivityLog.Error : ActivityLog.Info);
        }

        // Linha que não é JSON (ex.: erro antes do logger iniciar) vale inteira, com nível "info".
        static void ParseLogLine(string line, out string level, out string message)
        {
            level = "info";
            message = line;
            if (!line.StartsWith("{")) return;
            try
            {
                var entry = new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>;
                if (entry == null) return;
                object value;
                if (entry.TryGetValue("level", out value) && value != null) level = value.ToString().ToLowerInvariant();
                if (entry.TryGetValue("msg", out value) && value != null) message = value.ToString();
            }
            catch { }
        }

        async void OnExited(Process exited, int socksPort)
        {
            if (exited != process) return; // parado de propósito ou já substituído
            Active = false;
            Ipv6 = false;
            process = null;
            if (!wanted) return;
            // caiu sozinho: tenta de novo algumas vezes antes de desistir
            if ((DateTime.UtcNow - restartWindowStart).TotalMinutes > 5) { restartWindowStart = DateTime.UtcNow; restarts = 0; }
            if (++restarts > MaxRestarts)
            {
                wanted = false;
                Error = "o tun2socks parou várias vezes";
                log.Add("A captura parou várias vezes seguidas e foi desligada. Os programas voltaram à conexão normal.", ActivityLog.Error);
                return;
            }
            log.Add("A captura parou inesperadamente; reiniciando...", ActivityLog.Error);
            await Task.Delay(2000);
            if (wanted) await Launch(socksPort);
        }

        static async Task Netsh(string arguments)
        {
            var result = await Task.Run(() => ProcessRunner.Run("netsh.exe", arguments));
            var output = (result.StandardOutput + result.StandardError).Trim();
            // repetir um ajuste já aplicado (ex.: rota existente) não é erro
            if (result.ExitCode != 0 && !output.Contains("already exists") && !output.Contains("já existe"))
            {
                var words = arguments.Split(' ');
                throw new Exception("netsh " + words[0] + " " + words[2] + ": " + output);
            }
        }
    }
}
