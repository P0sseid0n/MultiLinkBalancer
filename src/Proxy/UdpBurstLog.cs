// Registra rajadas de destinos UDP novos. O tun2socks abre uma associação por destino: um programa que fala com
// milhares de destinos de uma vez (P2P, jogos, atualizações) aparece aqui. As portas e endereços mais comuns
// ajudam a descobrir qual programa foi. Vai para o registro de eventos e para rajadas-udp.log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using MultiLink.Common;
using MultiLink.Storage;

namespace MultiLink.Proxy
{
    class UdpBurstLog
    {
        const int WindowSeconds = 10, Threshold = 300, TopShown = 6, MaxAddressesTracked = 20000;
        static readonly string FilePath = Path.Combine(AppPaths.Local, "rajadas-udp.log");

        readonly ActivityLog log;
        readonly object sync = new object();
        readonly Dictionary<int, int> ports = new Dictionary<int, int>();
        readonly Dictionary<string, int> addresses = new Dictionary<string, int>();
        int flows;
        long windowStart = Stopwatch.GetTimestamp();

        public UdpBurstLog(ActivityLog log) { this.log = log; }

        public void NewFlow(IPEndPoint target)
        {
            string summary = null;
            lock (sync)
            {
                long now = Stopwatch.GetTimestamp();
                if (now - windowStart > Stopwatch.Frequency * WindowSeconds)
                {
                    if (flows >= Threshold) summary = Summary();
                    flows = 0;
                    ports.Clear();
                    addresses.Clear();
                    windowStart = now;
                }
                flows++;
                Count(ports, target.Port);
                var address = target.Address.ToString();
                if (addresses.Count < MaxAddressesTracked || addresses.ContainsKey(address)) Count(addresses, address);
            }
            if (summary == null) return;
            log.Add(summary, ActivityLog.Error);
            try { File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + summary + Environment.NewLine); } catch { }
        }

        string Summary()
        {
            return string.Format("Rajada de UDP: {0} destinos novos em {1} s ({2} endereços diferentes). Portas: {3}. Endereços: {4}.",
                flows, WindowSeconds, addresses.Count, Top(ports), Top(addresses));
        }

        static void Count<T>(Dictionary<T, int> counts, T key)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        static string Top<T>(Dictionary<T, int> counts)
        {
            return string.Join(", ", counts.OrderByDescending(pair => pair.Value).Take(TopShown).Select(pair => pair.Key + " (" + pair.Value + ")"));
        }
    }
}
