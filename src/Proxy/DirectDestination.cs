// Destinos que não saem por um link: o próprio PC (localhost, 0.0.0.0, os IPs dos adaptadores) e as redes que o
// Windows alcança por outro adaptador (WSL, Hyper-V, Docker, VPN, placa sem gateway). Presa ao IP de um link, a conexão
// nunca chegaria lá ("nenhum link conseguiu conectar"): o proxy conecta direto, pela rota do Windows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MultiLink.Links;
using MultiLink.Platform;

namespace MultiLink.Proxy
{
    static class DirectDestination
    {
        // Nome que é sempre o próprio PC. O Windows não resolve os subdomínios de .localhost (ex.: app.localhost), que
        // o navegador sem proxy manda para o próprio PC.
        public static IPAddress[] ForName(string host)
        {
            host = host.TrimEnd('.');
            bool local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
            return local ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : null;
        }

        // Endereços para conectar direto (IPv4 primeiro), ou null se o destino sai pelos links.
        public static IPAddress[] Find(IPAddress[] resolved, IEnumerable<Link> links)
        {
            // 0.0.0.0 e :: não são destinos de verdade: quem os acessa (ex.: http://0.0.0.0:3000 de um servidor de
            // desenvolvimento) quer o próprio PC, e é para lá que o Windows os manda sem o proxy
            var targets = resolved.Select(address => address.Equals(IPAddress.Any) ? IPAddress.Loopback : address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : address)
                                  .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            if (targets.Any(address => IPAddress.IsLoopback(address) || LocalRoutes.IsOwnAddress(address))) return targets;

            // rota mais específica que a padrão por um adaptador que não é link. As rotas da captura (/1) e a rota
            // padrão de uma VPN que leva tudo ficam de fora: esse tráfego continua balanceado, como antes
            var ipv4 = targets.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
            int interfaceIndex, prefixLength;
            if (ipv4.Length == 0 || !LocalRoutes.BestRouteV4(ipv4[0], out interfaceIndex, out prefixLength) || prefixLength <= 1) return null;
            return links.Any(link => link.InterfaceIndex == interfaceIndex) ? null : ipv4;
        }
    }
}
