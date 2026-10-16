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

        readonly Balancer balancer;
        readonly ActivityLog log;

        public BalancedConnector(Balancer balancer, ActivityLog log) { this.balancer = balancer; this.log = log; }

        // Devolve null se nenhum link conseguiu conectar (o motivo vai para o registro de eventos).
        public async Task<OutboundConnection> Connect(string host, int port)
        {
            IPAddress[] resolved;
            try
            {
                IPAddress literal;
                resolved = IPAddress.TryParse(host, out literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host);
            }
            catch (Exception ex) { log.Add("DNS falhou para " + host + ": " + ex.Message); return null; }

            // IPv4 é o padrão; IPv6 só para destinos sem IPv4, e apenas em links que tenham IPv6
            var targets = resolved.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Take(AddressesPerAttempt).ToArray();
            bool useIpv6 = targets.Length == 0;
            if (useIpv6) targets = resolved.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6).Take(AddressesPerAttempt).ToArray();
            if (targets.Length == 0) { log.Add("Nenhum endereço para " + host); return null; }

            var tried = new List<Link>();
            if (useIpv6) tried.AddRange(balancer.Snapshot().Where(link => link.AddressV6 == null));
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var link = balancer.Pick(host, tried);
                if (link == null)
                {
                    if (useIpv6 && attempt == 0) log.Add("Nenhum link com IPv6 para " + host);
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
                        await socket.ConnectWithTimeout(new IPEndPoint(target, port), ConnectTimeoutMs);
                        Interlocked.Increment(ref link.Traffic.TotalConnections);
                        return new OutboundConnection { Socket = socket, Link = link };
                    }
                    catch (Exception ex) { error = ex.Message; socket.CloseQuietly(); }
                }
                log.Add(string.Format("[{0}] falha ao conectar {1}:{2} ({3}){4}", link.Name, host, port, error,
                    attempt < MaxAttempts - 1 ? " - tentando outro link" : ""));
            }
            return null;
        }
    }
}
