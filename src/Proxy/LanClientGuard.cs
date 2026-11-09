// Com "Aceitar outros aparelhos da rede", o proxy atende qualquer aparelho da rede local. Os destinos que não saem
// por um link (DirectDestination: localhost, os IPs deste PC e as redes de outro adaptador, como WSL, Docker e VPN)
// ficam só para os programas do próprio PC: sem isso, um aparelho da rede chegaria pelo proxy a serviços que só
// escutam em 127.0.0.1 e a redes que ele não alcança sozinho. Cada aparelho aparece no registro uma vez a cada 10 min.
using System;
using System.Collections.Concurrent;
using System.Net;
using MultiLink.Common;
using MultiLink.Platform;

namespace MultiLink.Proxy
{
    class LanClientGuard
    {
        const int RepeatMinutes = 10;

        readonly ActivityLog log;
        readonly ConcurrentDictionary<IPAddress, DateTime> lastLogged = new ConcurrentDictionary<IPAddress, DateTime>();

        public LanClientGuard(ActivityLog log) { this.log = log; }

        // O cliente é um programa deste PC? (conectou pelo 127.0.0.1 ou por um IP dos adaptadores daqui)
        public static bool IsThisPc(IPAddress client)
        {
            if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
            return IPAddress.IsLoopback(client) || LocalRoutes.IsOwnAddress(client);
        }

        public void Refused(IPAddress client, string host, int port, string protocol)
        {
            var now = DateTime.UtcNow;
            DateTime previous;
            if (lastLogged.TryGetValue(client, out previous) && now - previous < TimeSpan.FromMinutes(RepeatMinutes)) return;
            lastLogged[client] = now;
            log.Add(string.Format("{0} (aparelho da rede) pediu {1}:{2} {3}, que fica neste PC ou numa rede de outro adaptador: recusado. " +
                                  "Pelo proxy, só os programas deste PC chegam a esses destinos.", client, host, port, protocol), ActivityLog.Error);
        }
    }
}
