// Acompanha o estado de cada link a cada segundo: avisa as quedas e voltas (HealthChanged) e
// registra quando um link fica instável por perda de pacotes e quando melhora, e quando o IPv6 dele sai e volta.
// Nas transições da suspensão do Windows (Suspend e Resume) nada é avisado.
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
        // Ao acordar, a placa de rede volta antes da internet: até este prazo, falha não é queda.
        static readonly TimeSpan WakeHold = TimeSpan.FromSeconds(20);
        // Adaptador recém-ativado: até este prazo para pegar IP e internet, ele aparece como "conectando", não como queda.
        static readonly TimeSpan ConnectHold = TimeSpan.FromSeconds(30);

        readonly ActivityLog log;
        bool asleep;
        readonly Dictionary<Link, DateTime> waking = new Dictionary<Link, DateTime>(); // link -> fim da espera (UTC)

        public event Action<Link, string, string> HealthChanged; // link, estado anterior, estado atual

        public LinkMonitor(ActivityLog log) { this.log = log; }

        // Suspensão do Windows: ao entrar, a placa de rede desliga antes do PC, e nada disso é avisado até o Resume.
        public void Suspend() { asleep = true; }

        // O estado de antes de dormir é esquecido: o link que voltar a funcionar dentro da espera não gera aviso,
        // e o que continuar fora depois dela é avisado como queda nova.
        public void Resume(List<Link> links)
        {
            asleep = false;
            var until = DateTime.UtcNow + WakeHold;
            foreach (var link in links)
            {
                waking[link] = until;
                link.DownSince = DateTime.Now;
                link.Quality.Reset(); // as sondas perdidas enquanto a placa acordava não são perda de pacotes
            }
        }

        public void Evaluate(List<Link> links, bool qualityAware)
        {
            foreach (var link in links)
            {
                if (Held(link)) continue;
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

        // Dormindo ou acordando: o estado aparece na tela, mas não é avisado. A espera ao acordar acaba no prazo
        // ou antes, quando o link volta a funcionar.
        bool Held(Link link)
        {
            if (asleep) { link.Health = LinkHealth.Of(link); return true; }
            DateTime until;
            if (!waking.TryGetValue(link, out until)) return false;
            var current = LinkHealth.Of(link);
            if (current != LinkHealth.Ok && DateTime.UtcNow < until) { link.Health = current; return true; }
            waking.Remove(link);
            link.Health = LinkHealth.Ok; // ainda fora: o EvaluateHealth avisa a queda a partir de agora
            return false;
        }

        void EvaluateHealth(Link link)
        {
            var current = LinkHealth.Of(link);
            var previous = link.Health;
            if (previous == LinkHealth.Disabled && current != LinkHealth.Disabled && current != LinkHealth.Ok)
            {
                // adaptador ativado (pelo app ou pelo Windows): esperando o IP e a internet, sem aviso
                link.ConnectingUntil = DateTime.UtcNow + ConnectHold;
                link.UserDisabled = false;
                current = LinkHealth.Connecting;
            }
            link.Health = current;
            if (previous == null) { if (current != LinkHealth.Ok) link.DownSince = DateTime.Now; return; } // primeira avaliação
            if (previous == current || current == LinkHealth.Connecting) return;
            if (previous == LinkHealth.Connecting)
            {
                link.ConnectingUntil = default(DateTime);
                if (current == LinkHealth.Disabled) return; // desativado de novo antes de conectar
                // conectou: avisado como a volta do adaptador desativado; o prazo acabou sem internet: queda nova
                previous = current == LinkHealth.Ok ? LinkHealth.Disabled : LinkHealth.Ok;
            }
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
