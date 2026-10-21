// Aparelho da rede local de um link que chegou pela captura. A rota da sub-rede local é mais específica que as
// rotas da captura, então o Windows só manda esse destino para o adaptador virtual quando não encontra o aparelho
// pela rede local. O tun2socks já completou a conexão (e responde ao ping) no lugar do aparelho: sem a recusa,
// um aparelho desligado parece ligado para o programa. Cada destino aparece no registro uma vez a cada 10 min.
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Proxy
{
    class LocalCaptureLog
    {
        const int RepeatMinutes = 10;

        readonly Balancer balancer;
        readonly ActivityLog log;
        readonly ConcurrentDictionary<string, DateTime> lastLogged = new ConcurrentDictionary<string, DateTime>();

        public LocalCaptureLog(Balancer balancer, ActivityLog log) { this.balancer = balancer; this.log = log; }

        // Conexão TCP da captura: true se o destino é da rede local de um link (o proxy recusa na hora, sem conectar).
        public bool RefuseTcp(string host, int port)
        {
            IPAddress target;
            if (!IPAddress.TryParse(host, out target)) return false;
            var link = LocalLink(target);
            if (link == null) return false;
            Register(target, port, link, "TCP", "conexão recusada na hora, o programa recebe \"host inalcançável\"");
            return true;
        }

        // Datagrama UDP da captura: só registra (sem conexão para recusar; segue pelo link da rede local).
        public void ArrivedUdp(IPEndPoint target, Link link)
        {
            Register(target.Address, target.Port, link, "UDP", "enviado pelo " + link.Name + ", sem garantia de chegar");
        }

        Link LocalLink(IPAddress target)
        {
            if (target.AddressFamily != AddressFamily.InterNetwork) return null;
            return balancer.Snapshot().FirstOrDefault(link => link.Present && link.OnLocalNetwork(target));
        }

        void Register(IPAddress target, int port, Link link, string protocol, string outcome)
        {
            var key = protocol + " " + target;
            var now = DateTime.UtcNow;
            DateTime previous;
            if (lastLogged.TryGetValue(key, out previous) && now - previous < TimeSpan.FromMinutes(RepeatMinutes)) return;
            lastLogged[key] = now;
            log.Add(string.Format("{0}:{1} {2} (rede local do {3}) chegou pela captura: o Windows não encontrou o aparelho na rede " +
                                  "(desligado, IP errado ou em outra rede?). {4}.", target, port, protocol, link.Name, outcome), ActivityLog.Error);
        }
    }
}
