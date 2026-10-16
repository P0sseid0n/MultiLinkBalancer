using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MultiLink.Links
{
    // Encontra os adaptadores do Windows que podem ser links: no ar, com IPv4 e gateway.
    public static class AdapterDiscovery
    {
        // ignoredAdapter: o adaptador virtual da captura, que não é um link
        public static List<Link> Find(string ignoredAdapter)
        {
            var found = new List<Link>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (adapter.Name == ignoredAdapter) continue;
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); } catch { continue; }

                var gateway = properties.GatewayAddresses.Select(entry => entry.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
                if (gateway == null) continue;
                var address4 = properties.UnicastAddresses.Select(entry => entry.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
                if (address4 == null) continue;

                found.Add(new Link
                {
                    Id = adapter.Id, Name = adapter.Name, Description = adapter.Description,
                    Address = address4, AddressV6 = GlobalIPv6(properties), InterfaceIndexV6 = IPv6Index(properties), Gateway = gateway.ToString(),
                    AdapterSpeedBps = adapter.Speed, Wired = IsWired(adapter.NetworkInterfaceType)
                });
            }
            return found;
        }

        // IPv6 que chega à internet: sem link-local, site-local ou ULA (fc00::/7). Prefere o endereço fixo ao temporário.
        static IPAddress GlobalIPv6(IPInterfaceProperties properties)
        {
            return properties.UnicastAddresses
                .Where(entry => entry.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                !entry.Address.IsIPv6LinkLocal && !entry.Address.IsIPv6SiteLocal &&
                                (entry.Address.GetAddressBytes()[0] & 0xFE) != 0xFC)
                .OrderBy(entry => entry.SuffixOrigin == SuffixOrigin.Random ? 1 : 0)
                .Select(entry => entry.Address).FirstOrDefault();
        }

        static int IPv6Index(IPInterfaceProperties properties)
        {
            try { var ipv6 = properties.GetIPv6Properties(); return ipv6 == null ? 0 : ipv6.Index; }
            catch (NetworkInformationException) { return 0; } // IPv6 desligado no adaptador
        }

        static bool IsWired(NetworkInterfaceType type)
        {
            return type == NetworkInterfaceType.Ethernet || type == NetworkInterfaceType.GigabitEthernet ||
                   type == NetworkInterfaceType.FastEthernetT || type == NetworkInterfaceType.FastEthernetFx ||
                   type == NetworkInterfaceType.Ethernet3Megabit;
        }
    }
}
