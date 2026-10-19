// Depois de uma rajada de conexões (ex.: milhares de fluxos UDP de um programa P2P ao ligar a captura), o que
// sobrou delas fica na geração 2: o GC em segundo plano não compacta essa geração nem devolve a memória ao
// Windows, e sem novas alocações o processo fica com centenas de MB parados. Compacta quando isso acontece.
using System;
using System.Diagnostics;
using System.Runtime;

namespace MultiLink.Common
{
    public static class MemoryTrim
    {
        const long TriggerBytes = 150L << 20;                         // uso normal fica em ~35 MB
        static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
        static readonly TimeSpan MinCompactInterval = TimeSpan.FromMinutes(5); // rajada longa: no máximo uma pausa a cada 5 min

        static DateTime lastCheck = DateTime.UtcNow, lastCompact = DateTime.MinValue;

        // Chamado a cada segundo; só olha a memória uma vez por minuto.
        public static void Tick()
        {
            var now = DateTime.UtcNow;
            if (now - lastCheck < CheckInterval || now - lastCompact < MinCompactInterval) return;
            lastCheck = now;
            using (var self = Process.GetCurrentProcess())
                if (self.PrivateMemorySize64 < TriggerBytes) return;
            lastCompact = now;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
    }
}
