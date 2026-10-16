// Escolhe por qual link sai cada nova conexão, conforme o modo de distribuição.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace MultiLink.Links
{
    public enum BalanceMode { WeightedRoundRobin = 0, LeastConnections = 1, Failover = 2, LowestLatency = 3 }

    public class Balancer
    {
        static readonly TimeSpan AffinityTtl = TimeSpan.FromMinutes(10);
        const int AffinityCleanupThreshold = 20000;

        readonly object sync = new object();
        readonly List<Link> links = new List<Link>();
        readonly Dictionary<string, AffinityEntry> affinity = new Dictionary<string, AffinityEntry>(StringComparer.OrdinalIgnoreCase);
        Link lowestLatencyChoice;

        public BalanceMode Mode = BalanceMode.WeightedRoundRobin;
        public bool Affinity;              // mantém cada site no mesmo link
        public bool QualityAware = true;   // perda de pacotes reduz o peso; perda e jitter pesam no modo "menor latência"

        struct AffinityEntry { public Link Link; public DateTime Expires; }

        public List<Link> Snapshot()
        {
            lock (sync) return links.ToList();
        }

        public void Mutate(Action<List<Link>> change)
        {
            lock (sync) change(links);
        }

        // host: destino da conexão (para a afinidade). exclude: links que já falharam ou não servem.
        public Link Pick(string host, ICollection<Link> exclude)
        {
            lock (sync)
            {
                var candidates = Candidates(exclude);
                if (candidates.Count == 0) return null;

                string affinityKey = Affinity ? AffinityKey(host) : null;
                if (affinityKey != null && exclude.Count == 0)
                {
                    AffinityEntry entry;
                    if (affinity.TryGetValue(affinityKey, out entry) && entry.Expires > DateTime.UtcNow && candidates.Contains(entry.Link))
                    {
                        Remember(affinityKey, entry.Link);
                        return entry.Link;
                    }
                }

                var chosen = Choose(candidates);
                // nos modos de um link só, a afinidade já acontece naturalmente
                if (affinityKey != null && Mode != BalanceMode.Failover && Mode != BalanceMode.LowestLatency)
                {
                    if (affinity.Count > AffinityCleanupThreshold) ForgetExpired();
                    Remember(affinityKey, chosen);
                }
                return chosen;
            }
        }

        List<Link> Candidates(ICollection<Link> exclude)
        {
            var usable = links.Where(link => link.Enabled && link.Present && link.Weight > 0 && !exclude.Contains(link)).ToList();
            var online = usable.Where(link => link.InternetUp).ToList();
            // a verificação de saúde pode dar falso negativo: sem nenhum link online, tenta qualquer link ativo
            return online.Count > 0 ? online : usable;
        }

        Link Choose(List<Link> candidates)
        {
            switch (Mode)
            {
                case BalanceMode.LeastConnections:
                    return candidates.OrderBy(link => (Thread.VolatileRead(ref link.Traffic.ActiveConnections) + 1) / (double)EffectiveWeight(link))
                                     .ThenBy(link => Interlocked.Read(ref link.Traffic.TotalConnections)).First();
                case BalanceMode.Failover:
                    return candidates.OrderByDescending(EffectiveWeight).First();
                case BalanceMode.LowestLatency:
                    return ChooseLowestLatency(candidates);
                default:
                    return ChooseWeightedRoundRobin(candidates);
            }
        }

        // Round-robin ponderado suave (algoritmo do nginx): distribui na proporção dos pesos sem rajadas.
        Link ChooseWeightedRoundRobin(List<Link> candidates)
        {
            int totalWeight = 0;
            Link chosen = null;
            foreach (var link in candidates)
            {
                int weight = EffectiveWeight(link);
                link.RoundRobinWeight += weight;
                totalWeight += weight;
                if (chosen == null || link.RoundRobinWeight > chosen.RoundRobinWeight) chosen = link;
            }
            chosen.RoundRobinWeight -= totalWeight;
            return chosen;
        }

        // O link atual só perde a vez se outro for claramente melhor (evita trocar a cada oscilação).
        Link ChooseLowestLatency(List<Link> candidates)
        {
            var best = candidates.OrderBy(LatencyScore).ThenByDescending(link => link.Weight).First();
            var current = lowestLatencyChoice;
            if (current != null && candidates.Contains(current) && current.LatencyAverage >= 0 &&
                LatencyScore(current) <= LatencyScore(best) * 1.2 + 3)
                best = current;
            lowestLatencyChoice = best;
            return best;
        }

        // Peso efetivo em décimos, para a redução pela qualidade não sumir no arredondamento de pesos pequenos.
        int EffectiveWeight(Link link)
        {
            if (link.Weight <= 0) return 0;
            return QualityAware ? Math.Max(1, (int)Math.Round(link.Weight * 10 * link.Quality.WeightFactor)) : link.Weight * 10;
        }

        // Nota para o modo "menor latência", em ms equivalentes (cada 1% de perda conta como 10 ms).
        double LatencyScore(Link link)
        {
            if (link.LatencyAverage < 0) return double.MaxValue;
            if (!QualityAware) return link.LatencyAverage;
            return link.LatencyAverage + Math.Max(0, link.Quality.JitterMs) + Math.Max(0, link.Quality.LossPct) * 10;
        }

        void Remember(string affinityKey, Link link)
        {
            affinity[affinityKey] = new AffinityEntry { Link = link, Expires = DateTime.UtcNow + AffinityTtl };
        }

        void ForgetExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var key in affinity.Where(pair => pair.Value.Expires < now).Select(pair => pair.Key).ToList())
                affinity.Remove(key);
        }

        // Agrupa subdomínios (www.google.com, mail.google.com -> google.com) para manter sessões no mesmo IP.
        static string AffinityKey(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return host;
            var labels = host.TrimEnd('.').Split('.');
            if (labels.Length <= 2) return host;
            // sufixos de dois níveis, como .com.br e .co.uk
            bool twoLevelSuffix = labels[labels.Length - 1].Length == 2 && labels[labels.Length - 2].Length <= 3;
            int keep = twoLevelSuffix ? 3 : 2;
            return string.Join(".", labels.Skip(Math.Max(0, labels.Length - keep)));
        }
    }
}
