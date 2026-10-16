// Qualidade de um link: uma amostra a cada verificação (~3 s); perda e jitter das últimas 20 (~1 min).
// A amostra é um ping (ICMP); se a rede bloqueia ICMP, vale o tempo da conexão TCP de teste.
using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiLink.Links
{
    public class LinkQuality
    {
        const int WindowSize = 20, MinSamples = 5;

        readonly Queue<int> samples = new Queue<int>(); // tempo de resposta em ms, -1 = perdido

        public volatile int LossPct = -1;   // -1 = sem dados suficientes
        public volatile int JitterMs = -1;
        public bool IcmpWorks;              // o ICMP já respondeu: as amostras vêm do ping
        public bool IcmpBlocked;            // o ICMP nunca respondeu em várias tentativas
        public int IcmpMisses;
        public bool ReportedUnstable;       // último aviso de instabilidade dado no registro de eventos

        // 1 = sem perda; cai rápido com a perda de pacotes (10% -> 0,67; 20% -> 0,33; 30% ou mais -> 0,05)
        public double WeightFactor
        {
            get { int loss = LossPct; return loss <= 0 ? 1 : Math.Max(0.05, 1 - loss / 30.0); }
        }

        public void AddSample(int responseMs)
        {
            lock (samples)
            {
                samples.Enqueue(responseMs);
                while (samples.Count > WindowSize) samples.Dequeue();
                var window = samples.ToArray();
                if (window.Length < MinSamples) { LossPct = -1; JitterMs = -1; return; }
                LossPct = (int)Math.Round(100.0 * window.Count(ms => ms < 0) / window.Length);
                // jitter: variação média entre respostas seguidas (como no RTP, RFC 3550)
                var answered = window.Where(ms => ms >= 0).ToArray();
                double variation = 0;
                for (int i = 1; i < answered.Length; i++) variation += Math.Abs(answered[i] - answered[i - 1]);
                JitterMs = answered.Length > 1 ? (int)Math.Round(variation / (answered.Length - 1)) : -1;
            }
        }

        public void Reset()
        {
            lock (samples)
            {
                samples.Clear();
                LossPct = -1;
                JitterMs = -1;
                IcmpWorks = IcmpBlocked = false;
                IcmpMisses = 0;
            }
        }
    }
}
