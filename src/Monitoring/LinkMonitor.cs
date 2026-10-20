// Acompanha o estado de cada link a cada segundo: avisa as quedas e voltas (HealthChanged) e
// registra quando um link fica instável por perda de pacotes e quando melhora, e quando o IPv6 dele sai e volta.
using System;
using System.Collections.Generic;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Monitoring
{
    public class LinkMonitor
    {
        // histerese: 10% de perda para avisar, 3% para considerar estável (evita um aviso a cada oscilação)
        const int UnstableLossPct = 10, StableLossPct = 3;

        readonly ActivityLog log;

        public event Action<Link, string, string> HealthChanged; // link, estado anterior, estado atual

        public LinkMonitor(ActivityLog log) { this.log = log; }

        public void Evaluate(List<Link> links, bool qualityAware)
        {
            foreach (var link in links)
            {
                EvaluateHealth(link);
                EvaluateStability(link, qualityAware);
                EvaluateIpv6(link);
            }
        }

        // O IPv6 sai de uso pelo teste ou por conexões reais (ver LinkIpv6): avisa uma vez a saída e uma vez a volta.
        // Um IPv6 que nunca funcionou não é avisado: o aviso da captura já diz se ela inclui o IPv6.
        void EvaluateIpv6(Link link)
        {
            var ipv6 = link.Ipv6;
            if (ipv6.Works)
            {
                ipv6.SeenWorking = true;
                if (!ipv6.ReportedDown) return;
                ipv6.ReportedDown = false;
                log.Add(string.Format("[{0}] IPv6 funcionando de novo: voltou a ser usado.", link.Name), ActivityLog.LinkUp);
            }
            else if (ipv6.SeenWorking && !ipv6.ReportedDown && link.AddressV6 != null && link.Present)
            {
                ipv6.ReportedDown = true;
                int minutes = (int)Math.Ceiling((ipv6.NextCheck - DateTime.UtcNow).TotalMinutes);
                log.Add(string.Format("[{0}] IPv6 instável: deixou de ser usado por este link (novo teste em {1} min).", link.Name, Math.Max(1, minutes)),
                    ActivityLog.Error);
            }
        }

        void EvaluateHealth(Link link)
        {
            var current = LinkHealth.Of(link);
            var previous = link.Health;
            link.Health = current;
            if (previous == null) { if (current != LinkHealth.Ok) link.DownSince = DateTime.Now; return; } // primeira avaliação
            if (previous == current) return;
            if (previous == LinkHealth.Ok) link.DownSince = DateTime.Now;
            if (current == LinkHealth.Ok && link.UserDisabled) link.UserDisabled = false;
            var handler = HealthChanged;
            if (handler != null) handler(link, previous, current);
        }

        void EvaluateStability(Link link, bool qualityAware)
        {
            var quality = link.Quality;
            int loss = quality.LossPct;
            if (!quality.ReportedUnstable && loss >= UnstableLossPct)
            {
                quality.ReportedUnstable = true;
                log.Add(string.Format("[{0}] instável: {1}% dos pacotes se perdendo{2}", link.Name, loss,
                    qualityAware ? ". Menos conexões vão por ele até melhorar." : "."), ActivityLog.Error);
            }
            else if (quality.ReportedUnstable && (loss < 0 ? !link.InternetUp : loss <= StableLossPct))
            {
                quality.ReportedUnstable = false;
                if (link.InternetUp) log.Add(string.Format("[{0}] estável de novo: {1}% de perda", link.Name, loss), ActivityLog.LinkUp);
            }
        }
    }
}
