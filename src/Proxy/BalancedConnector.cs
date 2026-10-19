// Abre a conexão com o destino pelo link que o Balancer escolher. Se o link falhar, tenta outro.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Proxy
{
    class OutboundConnection
    {
        public Socket Socket;
        public Link Link;
    }

    class BalancedConnector
    {
        const int MaxAttempts = 3;
        const int AddressesPerAttempt = 2;
        const int ConnectTimeoutMs = 8000;
        const int LocalConnectTimeoutMs = 3000; // aparelho da rede local responde rápido ou não está lá
        const int DnsRetryDelayMs = 1000;
        const int KeepAliveIdleMs = 30000, KeepAliveIntervalMs = 3000; // conexão morta detectada em ~1 min

        readonly Balancer balancer;
        readonly ActivityLog log;
        int warnedNoIpv6; // o aviso de "sem IPv6" sai uma vez só

        public BalancedConnector(Balancer balancer, ActivityLog log) { this.balancer = balancer; this.log = log; }

        // Devolve null se nenhum link conseguiu conectar (o motivo vai para o registro de eventos).
        public async Task<OutboundConnection> Connect(string host, int port)
        {
            IPAddress[] resolved;
            try
            {
                IPAddress literal;
                resolved = IPAddress.TryParse(host, out literal) ? new[] { literal } : await Resolve(host);
            }
            catch (Exception ex)
            {
                // o nome não existe ou não tem endereço: problema do destino, não dos links (o programa recebe o erro igual)
                if (!IsMissingName(ex)) log.Add("DNS falhou para " + host + ": " + ex.Message);
                return null;
            }

            // IPv4 é o padrão; IPv6 só para destinos sem IPv4, e apenas em links cujo IPv6 passou no teste
            var targets = resolved.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Take(AddressesPerAttempt).ToArray();
            bool useIpv6 = targets.Length == 0;
            if (useIpv6) targets = resolved.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6).Take(AddressesPerAttempt).ToArray();
            if (targets.Length == 0) { log.Add("Nenhum endereço para " + host); return null; }

            // destino na rede local de um link (modem, aparelho da casa): só ele alcança, nada de balancear
            var local = useIpv6 ? null : balancer.Snapshot().FirstOrDefault(link => link.Present && link.OnLocalNetwork(targets[0]));
            int maxAttempts = local != null ? 1 : MaxAttempts;

            var tried = new List<Link>();
            if (useIpv6) tried.AddRange(balancer.Snapshot().Where(link => !link.Ipv6Works));
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var link = local ?? balancer.Pick(host, tried);
                if (link == null)
                {
                    if (useIpv6 && attempt == 0 && Interlocked.Exchange(ref warnedNoIpv6, 1) == 0)
                        log.Add("Nenhum link com IPv6 funcionando: destinos só IPv6 (ex.: " + host + ") ficam sem conexão.");
                    break;
                }
                tried.Add(link);
                string error = null;
                foreach (var target in targets)
                {
                    var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        socket.Bind(new IPEndPoint(useIpv6 ? link.AddressV6 : link.Address, 0));
                        if (useIpv6) socket.UseInterfaceV6(link.InterfaceIndexV6);
                        socket.NoDelay = true;
                        socket.EnableKeepAlive(KeepAliveIdleMs, KeepAliveIntervalMs);
                        await socket.ConnectWithTimeout(new IPEndPoint(target, port), local != null ? LocalConnectTimeoutMs : ConnectTimeoutMs);
                        Interlocked.Increment(ref link.Traffic.TotalConnections);
                        return new OutboundConnection { Socket = socket, Link = link };
                    }
                    catch (Exception ex) { error = ex.Message; socket.CloseQuietly(); }
                }
                if (local != null) break; // aparelho da rede local desligado: não é falha do link
                log.Add(string.Format("[{0}] falha ao conectar {1}:{2} ({3}){4}", link.Name, host, port, error,
                    attempt < maxAttempts - 1 ? " - tentando outro link" : ""));
            }
            return null;
        }

        // Logo que a captura liga, o DNS do Windows passa a sair pelo adaptador virtual e as primeiras
        // consultas podem falhar ("no data of the requested type"). Uma segunda tentativa resolve.
        static bool IsMissingName(Exception ex)
        {
            var socketError = ex as SocketException;
            return socketError != null && (socketError.SocketErrorCode == SocketError.HostNotFound || socketError.SocketErrorCode == SocketError.NoData);
        }

        static async Task<IPAddress[]> Resolve(string host)
        {
            try { return await Dns.GetHostAddressesAsync(host); }
            catch (SocketException ex) { if (ex.SocketErrorCode == SocketError.HostNotFound) throw; }
            await Task.Delay(DnsRetryDelayMs);
            return await Dns.GetHostAddressesAsync(host);
        }
    }
}
