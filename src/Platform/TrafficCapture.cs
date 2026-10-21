// Captura de todo o tráfego IPv4 e IPv6 (inclusive programas que ignoram o proxy do Windows).
// O tun2socks cria um adaptador virtual Wintun ("MultiLink") e entrega cada conexão TCP/UDP
// que chega nele ao proxy SOCKS5 local do MultiLink. Duas rotas /1 (mais específicas que a rota
// padrão) mandam a internet para esse adaptador; as conexões do próprio MultiLink saem presas ao
// IP e à placa de cada link, então continuam indo direto pelos adaptadores reais (modelo "strong host"; ver StrongHost).
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
        // O adaptador virtual não manda nada para a rede (cada conexão sai de novo por um socket do link), então o
        // limite de 1500 dos links não vale para ele. Pacotes maiores são bem menos pacotes para o tun2socks
        // processar: o net-merge mediu cerca de 3x menos CPU por Mbps com 9000. Não 65535: o tun2socks reserva um
        // buffer do tamanho do MTU para cada pacote que chega, até para um ACK de 40 bytes.
        const int Mtu = 9000;
        const int MaxRestarts = 3;

        readonly ActivityLog log;
        readonly StrongHost strongHost;
        readonly Ipv6Block ipv6Block;
        readonly JobObject job = new JobObject();
        Process process;
        volatile bool wanted;
        Func<bool> anyLinkHasIpv6;
        Func<bool> ipv6Allowed;
        Func<IEnumerable<int>> linkInterfaces;
        int restarts;
        bool syncingIpv6;
        bool ipv6Failed; // o netsh recusou: não insiste até a captura ser religada
        DateTime restartWindowStart;

        DateTime windowsEndingAt = DateTime.MinValue;

        public TrafficCapture(ActivityLog log, Storage.AppSettings settings)
        {
            this.log = log;
            strongHost = new StrongHost(settings, log);
            ipv6Block = new Ipv6Block(log);
            // no desligamento o Windows encerra o tun2socks, às vezes antes de o MultiLink começar a fechar
            Microsoft.Win32.SystemEvents.SessionEnding += (sender, e) => windowsEndingAt = DateTime.UtcNow;
        }

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

        // Na abertura: um fechamento inesperado com a captura ligada deixa o weak host send dos links desligado.
        // A regra do firewall que recusa o IPv6 (Ipv6Block) também pode ter ficado.
        public void RestoreLeftovers() { strongHost.RestoreAll(); var ignored = ipv6Block.Set(false, AdapterName); }

        // anyLinkHasIpv6: sem um link com IPv6 funcionando, rotear IPv6 para o adaptador deixaria esses destinos sem saída.
        // linkInterfaces: índices IPv4 e IPv6 dos adaptadores dos links (ver StrongHost).
        // ipv6Allowed: se o balanceamento aceita conexões IPv6 agora; se não, a captura as recusa no início (ver Ipv6Block).
        public async Task Start(int socksPort, Func<bool> anyLinkHasIpv6, Func<IEnumerable<int>> linkInterfaces, Func<bool> ipv6Allowed)
        {
            if (wanted) return;
            this.anyLinkHasIpv6 = anyLinkHasIpv6;
            this.ipv6Allowed = ipv6Allowed;
            this.linkInterfaces = linkInterfaces;
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
            strongHost.RestoreAll(); // depois de tirar a captura: sem as rotas dela, o weak host não atrapalha mais
            ipv6Block.RemoveNow();
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
                await strongHost.Apply(linkInterfaces(), true); // antes das rotas: as conexões do MultiLink precisam sair certo desde o início
                if (!wanted) { strongHost.RestoreAll(); return; } // desligada enquanto isso
                await ConfigureIpv4();
                Ipv6 = anyLinkHasIpv6 != null && anyLinkHasIpv6() && await TryConfigureIpv6();

                if (!wanted || started.HasExited) return;
                Active = true;
                log.Add("Captura ligada" + (Ipv6 ? " (IPv4 e IPv6)" : "") + ": todos os programas (inclusive jogos e apps que ignoram o proxy) passam pelo MultiLink.");
                TidyNetworkProfiles();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                log.Add("Não foi possível capturar todos os programas: " + ex.Message, ActivityLog.Error);
                wanted = false;
                var failed = process;
                process = null;
                try { if (failed != null && !failed.HasExited) failed.Kill(); } catch { }
                strongHost.RestoreAll();
            }
            finally { Starting = false; }
        }

        Process StartTun2Socks(int socksPort)
        {
            var folder = EmbeddedFiles.ExtractTun2Socks(ExeName);
            KillStale();
            var startInfo = new ProcessStartInfo(Path.Combine(folder, ExeName),
                "--device tun://" + AdapterName + "?guid=" + AdapterGuid + " --proxy socks5://" + Proxy.Socks5Handshake.CaptureUser + ":x@127.0.0.1:" + socksPort + " --mtu " + Mtu + " --loglevel warn --udp-timeout 60s")
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

        // Não atrapalha a captura: se falhar, só ficam os perfis antigos no registro.
        async void TidyNetworkProfiles()
        {
            try
            {
                int removed = await NetworkProfiles.Tidy(AdapterName);
                if (removed > 0) log.Add((removed == 1 ? "Removido 1 perfil de rede antigo" : "Removidos " + removed + " perfis de rede antigos") + " do adaptador " + AdapterName + ".");
            }
            catch (Exception ex) { log.Add("Não foi possível limpar os perfis de rede antigos: " + ex.Message, ActivityLog.Error); }
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
            await Netsh("interface ipv4 set subinterface " + adapter + " mtu=" + Mtu + " store=active");
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
                await Netsh("interface ipv6 set subinterface " + adapter + " mtu=" + Mtu + " store=active");
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
            if (Active && ipv6Allowed != null) await SyncIpv6Block();
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

        async Task SyncIpv6Block()
        {
            bool block = !ipv6Allowed();
            if (block == ipv6Block.Blocked) return;
            await ipv6Block.Set(block, AdapterName);
            if (Active && ipv6Block.Blocked == block && block)
                log.Add("Conexões IPv6 dos programas capturados são recusadas no início (Firewall do Windows): eles usam o IPv4.");
        }

        // Chamado a cada segundo (a StrongHost verifica a cada 10 s): um link que aparece com a captura ligada, ou um
        // weak host que o Windows religou, também precisa ser desligado.
        public async void SyncLinks()
        {
            if (!Active) return;
            await strongHost.Apply(linkInterfaces(), false);
            if (!wanted) strongHost.RestoreAll(); // desligada enquanto isso
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
            // Windows desligando: não é queda. Se o desligamento for cancelado, religa sem contar como tentativa.
            if (DateTime.UtcNow - windowsEndingAt < TimeSpan.FromMinutes(1))
            {
                await Task.Delay(2000);
                if (wanted) await Launch(socksPort);
                return;
            }
            // caiu sozinho: tenta de novo algumas vezes antes de desistir
            if ((DateTime.UtcNow - restartWindowStart).TotalMinutes > 5) { restartWindowStart = DateTime.UtcNow; restarts = 0; }
            if (++restarts > MaxRestarts)
            {
                wanted = false;
                strongHost.RestoreAll();
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
