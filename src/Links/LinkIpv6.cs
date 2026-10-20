// Se vale mandar IPv6 por um link. Muita operadora entrega IPv6 global que funciona mal: o teste periódico
// (HealthChecker) pode passar por sorte enquanto as conexões reais esgotam o tempo. Por isso o IPv6 sai de uso
// tanto pelo teste quanto por conexões reais sem resposta, e cada nova queda dobra a espera até o próximo teste,
// para um IPv6 que oscila não ficar entrando e saindo (nem enchendo o registro de eventos).
using System;
using System.Collections.Generic;

namespace MultiLink.Links
{
    public class LinkIpv6
    {
        const int TimeoutsUntilBad = 3; // destinos diferentes sem resposta, sem nenhuma conexão boa entre eles
        static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(2);
        static readonly TimeSpan FirstPenalty = TimeSpan.FromMinutes(5), MaxPenalty = TimeSpan.FromMinutes(60);
        static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(30); // funcionando esse tempo, as quedas antigas são esquecidas

        readonly object sync = new object();
        readonly HashSet<string> timedOut = new HashSet<string>();
        int penalties;
        DateTime nextCheck, workingSince;

        public volatile bool Works;   // o IPv6 global do link chega mesmo à internet (testado)
        public bool SeenWorking;      // já funcionou alguma vez (LinkMonitor: só avisa queda do que funcionava)
        public bool ReportedDown;     // último aviso dado no registro de eventos

        public DateTime NextCheck { get { lock (sync) return nextCheck; } }

        // Chegou a hora do teste periódico? Se sim, já reserva: outra verificação do mesmo link não testa junto.
        public bool TryStartCheck(DateTime now)
        {
            lock (sync)
            {
                if (now < nextCheck) return false;
                nextCheck = now + RecheckInterval;
                return true;
            }
        }

        // Resultado do teste periódico.
        public void Checked(bool works, DateTime now)
        {
            lock (sync)
            {
                if (works)
                {
                    if (!Works) { workingSince = now; timedOut.Clear(); }
                    else if (penalties > 0 && now - workingSince >= StableAfter) penalties = 0;
                    Works = true;
                    nextCheck = now + RecheckInterval;
                }
                else if (Works) MarkBad(now);
                else nextCheck = now + Backoff();
            }
        }

        // Conexão real que esgotou o tempo. Um destino fora do ar não conta várias vezes: precisa de destinos diferentes.
        public void ConnectTimedOut(string destination, DateTime now)
        {
            lock (sync)
            {
                if (!Works) return;
                timedOut.Add(destination);
                if (timedOut.Count >= TimeoutsUntilBad) MarkBad(now);
            }
        }

        public void ConnectSucceeded() { lock (sync) timedOut.Clear(); }

        // Endereço IPv6 do adaptador mudou (ou sumiu): o histórico do anterior não vale, testa de novo já.
        public void AddressChanged(bool hasAddress)
        {
            lock (sync)
            {
                if (!hasAddress) Works = false;
                penalties = 0;
                nextCheck = DateTime.MinValue;
                timedOut.Clear();
            }
        }

        void MarkBad(DateTime now)
        {
            Works = false;
            penalties++;
            nextCheck = now + Backoff();
            timedOut.Clear();
        }

        TimeSpan Backoff()
        {
            if (penalties == 0) return RecheckInterval;
            var wait = TimeSpan.FromTicks(FirstPenalty.Ticks << Math.Min(penalties - 1, 10));
            return wait < MaxPenalty ? wait : MaxPenalty;
        }
    }
}
