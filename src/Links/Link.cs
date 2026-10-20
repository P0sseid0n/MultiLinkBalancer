using System;
using System.Net;

namespace MultiLink.Links
{
    // Uma conexão de internet (adaptador de rede com gateway) usada pelo balanceamento.
    // Os campos são lidos e escritos por várias threads (proxy, verificação de saúde, interface).
    public class Link
    {
        // ---- adaptador (vem do Windows; ver AdapterDiscovery)
        public string Id;
        public string Name;
        public string Description;
        public IPAddress Address;
        public IPAddress SubnetMask;             // máscara da rede local do Address
        public IPAddress AddressV6;              // IPv6 global, se houver
        public int InterfaceIndexV6;             // índice IPv6 do adaptador (para prender o socket a ele)
        public string Gateway;
        public long AdapterSpeedBps;             // velocidade nominal informada pelo Windows
        public bool Wired;
        public volatile bool Present = true;     // o adaptador está no ar com IP e gateway
        public volatile string AdapterState;     // quando ausente: LinkHealth.Unplugged, Disabled ou NoConfig

        // ---- escolhas do usuário (gravadas pelo LinkManager)
        public volatile bool Enabled = true;
        public volatile int Weight = 1;
        public double MeasuredMbps = -1;

        // ---- saúde (HealthChecker e LinkMonitor)
        public volatile bool InternetUp = true;
        public volatile int LatencyMs = -1;
        public double LatencyAverage = -1;       // média móvel, usada no modo "menor latência"
        public int ConsecutiveFailures;
        public volatile bool GatewayReachable = true;
        public string Health;                    // último estado avaliado pelo LinkMonitor (LinkHealth)
        public DateTime DownSince;
        public volatile bool UserDisabled;       // desativado pelo próprio app: a queda não gera alerta
        public volatile bool MetricRaised;       // retirado do roteamento do Windows pelo RouteFailover
        public volatile bool MetricBusy;         // alteração de rota em andamento

        public readonly LinkQuality Quality = new LinkQuality();
        public readonly LinkTraffic Traffic = new LinkTraffic();
        public readonly LinkIpv6 Ipv6 = new LinkIpv6();

        // estado do round-robin suave do Balancer
        public int RoundRobinWeight;

        // Endereço IPv4 na rede local deste link (ex.: o modem): só é alcançável por ele.
        public bool OnLocalNetwork(IPAddress target)
        {
            var address = Address; var mask = SubnetMask;
            if (mask == null || address == null || target.AddressFamily != address.AddressFamily) return false;
            byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes(), t = target.GetAddressBytes();
            if (m.Length != t.Length) return false;
            for (int i = 0; i < t.Length; i++) if ((a[i] & m[i]) != (t[i] & m[i])) return false;
            return true;
        }
    }
}
