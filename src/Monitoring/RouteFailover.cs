// Failover para o Windows inteiro (opção "Tirar link caído do roteamento"): um link fora do ar,
// mas com cabo, recebe métrica alta enquanto houver outro link saudável, e o sistema todo passa a
// usar os outros. Quando ele volta, a métrica original é devolvida.
// A métrica original fica no config.ini ("metric.<id>" = "nome|valor") até ser restaurada, para
// que um fechamento inesperado não deixe o Windows com rotas alteradas.
using System.Collections.Generic;
using System.Linq;
using MultiLink.Common;
using MultiLink.Links;
using MultiLink.Platform;
using MultiLink.Storage;

namespace MultiLink.Monitoring
{
    public class RouteFailover
    {
        const string KeyPrefix = "metric.";

        readonly AppSettings settings;
        readonly ActivityLog log;

        public bool Enabled;

        public RouteFailover(AppSettings settings, ActivityLog log) { this.settings = settings; this.log = log; }

        public bool HasPendingRestore { get { return settings.KeysStartingWith(KeyPrefix).Count > 0; } }

        // Chamado a cada segundo, depois do LinkMonitor.
        public void Apply(List<Link> links)
        {
            if (!Enabled || !Elevation.IsAdmin) return;
            bool anyHealthy = links.Any(link => link.Enabled && link.Health == LinkHealth.Ok);
            foreach (var link in links)
            {
                if (link.MetricBusy || !link.Enabled) continue;
                bool shouldRaise = link.Present && link.Health != LinkHealth.Ok && anyHealthy;
                if (shouldRaise && !link.MetricRaised) Raise(link);
                else if (!shouldRaise && link.MetricRaised && link.Health == LinkHealth.Ok) Restore(link);
            }
        }

        async void Raise(Link link)
        {
            link.MetricBusy = true;
            try
            {
                var result = await RouteMetrics.Raise(link.Name);
                if (result.Ok)
                {
                    var key = KeyPrefix + link.Id;
                    if (!settings.Has(key)) { settings.Set(key, link.Name + "|" + result.Output); settings.Save(); }
                    link.MetricRaised = true;
                    log.Add("[" + link.Name + "] retirado do roteamento do Windows: o sistema inteiro passa a usar os outros links");
                }
                else log.Add("[" + link.Name + "] não foi possível retirar do roteamento: " + result.Output);
            }
            finally { link.MetricBusy = false; }
        }

        async void Restore(Link link)
        {
            link.MetricBusy = true;
            try
            {
                var key = KeyPrefix + link.Id;
                var saved = settings.Get(key, null);
                var result = await RouteMetrics.Restore(link.Name, saved == null ? null : saved.Substring(saved.IndexOf('|') + 1));
                if (result.Ok)
                {
                    settings.Remove(key);
                    settings.Save();
                    link.MetricRaised = false;
                    log.Add("[" + link.Name + "] devolvido ao roteamento do Windows");
                }
                else log.Add("[" + link.Name + "] não foi possível restaurar a rota: " + result.Output);
            }
            finally { link.MetricBusy = false; }
        }

        // Ao sair, ao desligar a opção ou ao abrir após um fechamento inesperado: devolve todas as métricas alteradas.
        public void RestoreAll(IEnumerable<Link> links)
        {
            foreach (var key in settings.KeysStartingWith(KeyPrefix))
            {
                var saved = settings.Get(key, "");
                int separator = saved.IndexOf('|');
                if (separator <= 0) { settings.Remove(key); continue; }
                var restore = RouteMetrics.Restore(saved.Substring(0, separator), saved.Substring(separator + 1));
                if (restore.Wait(15000) && restore.Result.Ok) settings.Remove(key);
            }
            settings.Save();
            foreach (var link in links) link.MetricRaised = false;
        }
    }
}
