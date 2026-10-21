// Verifica se um link chega à internet: conexão TCP de teste a servidores conhecidos, saindo pelo
// IP do link. Também colhe a amostra de qualidade (perda e jitter) e, quando a internet não
// responde, testa o gateway para separar "problema na operadora" de "modem desligado/travado".
// O IPv6 é testado à parte: muita operadora entrega endereço IPv6 global que não chega a lugar nenhum.
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Monitoring
{
    public static class HealthChecker
    {
        static readonly IPEndPoint[] Probes =
        {
            new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443),
            new IPEndPoint(IPAddress.Parse("9.9.9.9"), 443),
        };
        static readonly IPEndPoint[] ProbesV6 =
        {
            new IPEndPoint(IPAddress.Parse("2606:4700:4700::1111"), 443),
            new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), 443),
        };
        static readonly IPAddress PingTarget = IPAddress.Parse("1.1.1.1");

        const int ProbeTimeoutMs = 3000;
        const int SlowAsLostMs = 900;      // o Windows reenvia o SYN após ~1 s: resposta lenta assim conta como perda
        const int FailuresUntilDown = 2;
        const int IcmpTriesBeforeBlocked = 5;

        public static async Task Check(Link link)
        {
            if (!link.Present) return;
            bool lostEarlierProbe = false;
            foreach (var probe in Probes)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    socket.BindForConnect(link.Address);
                    socket.UseInterfaceV4(link.InterfaceIndex);
                    await socket.ConnectWithTimeout(probe, ProbeTimeoutMs);
                    MarkOnline(link, (int)stopwatch.ElapsedMilliseconds);
                    await SampleQuality(link, lostEarlierProbe || link.LatencyMs >= SlowAsLostMs ? -1 : link.LatencyMs);
                    await CheckIpv6(link);
                    return;
                }
                catch { lostEarlierProbe = true; }
                finally { socket.CloseQuietly(); }
            }
            link.GatewayReachable = await PingGateway(link.Gateway);
            link.ConsecutiveFailures++;
            if (link.ConsecutiveFailures >= FailuresUntilDown && link.InternetUp) MarkOffline(link);
        }

        // Sem internet no IPv4 o link já sai do balanceamento; aqui só importa se vale mandar IPv6 por ele.
        // Precisam responder todas as sondas: num IPv6 instável, uma de duas responde por sorte (ver LinkIpv6).
        static async Task CheckIpv6(Link link)
        {
            var address = link.AddressV6;
            if (address == null) { link.Ipv6.Works = false; return; }
            if (!link.Ipv6.TryStartCheck(DateTime.UtcNow)) return;
            bool works = true;
            foreach (var probe in ProbesV6)
            {
                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.BindForConnect(address);
                    socket.UseInterfaceV6(link.InterfaceIndexV6);
                    await socket.ConnectWithTimeout(probe, ProbeTimeoutMs);
                }
                catch { works = false; break; }
                finally { socket.CloseQuietly(); }
            }
            link.Ipv6.Checked(works, DateTime.UtcNow);
        }

        static void MarkOnline(Link link, int latencyMs)
        {
            link.LatencyMs = latencyMs;
            link.LatencyAverage = link.LatencyAverage < 0 ? latencyMs : link.LatencyAverage * 0.7 + latencyMs * 0.3;
            link.ConsecutiveFailures = 0;
            link.GatewayReachable = true;
            link.InternetUp = true;
        }

        static void MarkOffline(Link link)
        {
            link.InternetUp = false;
            link.LatencyMs = -1;
            link.LatencyAverage = -1;
            link.Quality.Reset(); // ao voltar, a qualidade é medida do zero
        }

        // tcpMs: tempo da conexão de teste, ou -1 se ela se perdeu. Vale o ping se a rede responde ICMP.
        static async Task SampleQuality(Link link, int tcpMs)
        {
            var quality = link.Quality;
            int icmpMs = -1;
            if (!quality.IcmpBlocked)
            {
                var source = link.Address;
                icmpMs = await Task.Run(() => IcmpPing.Echo(source, PingTarget, 1000));
                if (icmpMs >= 0 && !quality.IcmpWorks) { quality.Reset(); quality.IcmpWorks = true; } // passa a usar o ICMP: recomeça a janela
                else if (!quality.IcmpWorks && ++quality.IcmpMisses >= IcmpTriesBeforeBlocked) quality.IcmpBlocked = true;
            }
            quality.AddSample(quality.IcmpWorks ? icmpMs : tcpMs);
        }

        static async Task<bool> PingGateway(string gateway)
        {
            IPAddress address;
            if (!IPAddress.TryParse(gateway, out address)) return false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var ping = new Ping())
                        if ((await ping.SendPingAsync(address, 1000)).Status == IPStatus.Success) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
