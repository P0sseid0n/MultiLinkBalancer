// Captura de todo o tráfego IPv4 e IPv6 (inclusive programas que ignoram o proxy do Windows).
// O tun2socks cria um adaptador virtual Wintun ("MultiLink") e entrega cada conexão TCP/UDP
// que chega nele ao proxy SOCKS5 local do MultiLink. Duas rotas /1 (mais específicas que a rota
// padrão) mandam a internet para esse adaptador; as conexões do próprio MultiLink saem presas ao
// IP de cada link, então continuam indo direto pelos adaptadores reais (modelo "strong host").
// A rede local continua direta porque as rotas das sub-redes locais são mais específicas que /1.
// IPv6 (só quando algum link tem IPv6 global): a rota 2000::/3 cobre toda a internet IPv6 e é mais
// específica que a padrão (::/0). O adaptador recebe um endereço ULA; com ele o Windows prefere
// IPv4 nos destinos que têm os dois, e os destinos só IPv6 seguem pelo MultiLink.
//
// Segurança: o tun2socks roda num Job Object com KILL_ON_JOB_CLOSE. Se o MultiLink fechar ou
// travar, o Windows encerra o tun2socks, o adaptador some e as rotas somem junto com ele.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MultiLink
{
    public class Capture
    {
        public const string AdapterName = "MultiLink";
        const string TunAddress = "198.18.0.1"; // faixa reservada para testes (RFC 2544): não colide com redes reais
        const string TunAddress6 = "fd4d:4c42::1"; // ULA (rede privada IPv6, RFC 4193)
        const string ExeName = "multilink-tun2socks.exe";

        readonly Action<string, string> log;
        Process proc;
        IntPtr job;
        volatile bool wanted;
        int restarts;
        DateTime restartWindow;

        public Capture(Action<string, string> log) { this.log = log; }

        public bool Active { get; private set; }
        public bool Starting { get; private set; }
        public string Error { get; private set; }
        public bool Ipv6 { get; private set; } // o tráfego IPv6 também está sendo capturado
        Func<bool> wantIpv6;

        // Encerra um tun2socks que tenha sobrado de uma execução anterior (não deveria, por causa do Job).
        public static void KillStale()
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        // hasIpv6: algum link tem IPv6 global? Sem isso, rotear IPv6 para o adaptador deixaria esses destinos sem saída.
        public async Task Start(int socksPort, Func<bool> hasIpv6)
        {
            if (wanted) return;
            wantIpv6 = hasIpv6;
            wanted = true;
            Error = null;
            restarts = 0;
            await Launch(socksPort);
        }

        public void Stop()
        {
            wanted = false;
            Active = false;
            Starting = false;
            Ipv6 = false;
            var p = proc;
            proc = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
            log("Captura de todos os programas desligada. O adaptador virtual foi removido.", "info");
        }

        async Task Launch(int socksPort)
        {
            Starting = true;
            try
            {
                if (!WinNet.IsAdmin) throw new Exception("requer o MultiLink aberto como administrador");
                var dir = Program.ExtractTun2Socks(ExeName);
                KillStale();

                var psi = new ProcessStartInfo(Path.Combine(dir, ExeName),
                    "--device tun://" + AdapterName + " --proxy socks5://127.0.0.1:" + socksPort + " --loglevel warn --udp-timeout 60s")
                {
                    WorkingDirectory = dir, // o wintun.dll é carregado da mesma pasta
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => Relay(e.Data);
                p.ErrorDataReceived += (s, e) => Relay(e.Data);
                p.Exited += (s, e) => OnExited(p, socksPort);
                p.Start();
                AttachToJob(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                proc = p;

                // espera o adaptador aparecer
                NetworkInterface ni = null;
                for (int i = 0; i < 60 && ni == null; i++)
                {
                    if (p.HasExited) throw new Exception("o tun2socks fechou ao iniciar (código " + p.ExitCode + ")");
                    await Task.Delay(250);
                    ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Name == AdapterName);
                }
                if (ni == null) throw new Exception("o adaptador virtual não apareceu");

                // endereço, DNS e rotas (store=active: nada fica salvo no Windows)
                var q = "\"" + AdapterName + "\"";
                await Netsh("interface ipv4 set address name=" + q + " source=static address=" + TunAddress + " mask=255.255.255.0");
                await Netsh("interface ipv4 set dnsservers name=" + q + " source=static address=1.1.1.1 register=none validate=no");
                await Netsh("interface ipv4 add dnsservers name=" + q + " address=8.8.8.8 index=2 validate=no");
                await Netsh("interface ipv4 set interface interface=" + q + " metric=1");
                await Netsh("interface ipv4 add route prefix=0.0.0.0/1 interface=" + q + " nexthop=" + TunAddress + " metric=1 store=active");
                await Netsh("interface ipv4 add route prefix=128.0.0.0/1 interface=" + q + " nexthop=" + TunAddress + " metric=1 store=active");

                Ipv6 = false;
                if (wantIpv6 != null && wantIpv6())
                {
                    // falhar aqui não derruba a captura IPv4: o IPv6 só continua direto, como antes
                    try
                    {
                        await Netsh("interface ipv6 add address interface=" + q + " address=" + TunAddress6 + " store=active");
                        await Netsh("interface ipv6 set interface interface=" + q + " metric=1");
                        await Netsh("interface ipv6 add route prefix=2000::/3 interface=" + q + " metric=1 store=active");
                        Ipv6 = true;
                    }
                    catch (Exception ex) { log("A captura vale só para IPv4; o IPv6 segue direto (" + ex.Message + ").", "error"); }
                }

                if (!wanted || p.HasExited) return;
                Active = true;
                log("Captura ligada" + (Ipv6 ? " (IPv4 e IPv6)" : "") + ": todos os programas (inclusive jogos e apps que ignoram o proxy) passam pelo MultiLink.", "info");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                log("Não foi possível capturar todos os programas: " + ex.Message, "error");
                wanted = false;
                var p = proc; proc = null;
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            }
            finally { Starting = false; }
        }

        void Relay(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // o tun2socks registra cada conexão que falhou; só repassa o que não é rotina
            if (line.Contains("[WARN]") && (line.Contains("dial") || line.Contains("i/o timeout"))) return;
            log("[captura] " + line.Trim(), line.Contains("[ERR") || line.Contains("FATAL") ? "error" : "info");
        }

        async void OnExited(Process p, int socksPort)
        {
            if (p != proc) return; // parado de propósito ou já substituído
            Active = false;
            Ipv6 = false;
            proc = null;
            if (!wanted) return;
            // caiu sozinho: tenta de novo algumas vezes antes de desistir
            if ((DateTime.UtcNow - restartWindow).TotalMinutes > 5) { restartWindow = DateTime.UtcNow; restarts = 0; }
            if (++restarts > 3)
            {
                wanted = false;
                Error = "o tun2socks parou várias vezes";
                log("A captura parou várias vezes seguidas e foi desligada. Os programas voltaram à conexão normal.", "error");
                return;
            }
            log("A captura parou inesperadamente; reiniciando...", "error");
            await Task.Delay(2000);
            if (wanted) await Launch(socksPort);
        }

        static async Task Netsh(string args)
        {
            var r = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("netsh.exe", args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return Tuple.Create(p.ExitCode, o.Trim());
                }
            });
            // repetir um ajuste já aplicado (ex.: rota existente) não é erro
            if (r.Item1 != 0 && !r.Item2.Contains("already exists") && !r.Item2.Contains("já existe"))
                throw new Exception("netsh " + args.Split(' ')[0] + " " + args.Split(' ')[2] + ": " + r.Item2);
        }

        // ------------------------------------------------------------------ Job Object
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
            public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        void AttachToJob(Process p)
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null); // o handle fica aberto até o MultiLink terminar
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
            }
            if (!AssignProcessToJobObject(job, p.Handle))
                log("Aviso: não foi possível vincular o tun2socks ao MultiLink; se o app travar, encerre o processo " + ExeName + " manualmente.", "error");
        }
    }
}
