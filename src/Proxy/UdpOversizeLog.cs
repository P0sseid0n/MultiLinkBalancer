// Avisa quando um datagrama UDP é descartado por ser maior que o buffer (BufferPool.Datagram). Na internet isso
// quase nunca acontece; se aparecer aqui, algum programa manda datagramas grandes e o limite precisa subir.
// O primeiro descarte avisa na hora; os seguintes se juntam num aviso a cada minuto, no máximo.
using System.Diagnostics;
using MultiLink.Common;

namespace MultiLink.Proxy
{
    class UdpOversizeLog
    {
        const int MinIntervalSeconds = 60;

        readonly ActivityLog log;
        readonly object sync = new object();
        int fromPrograms, fromInternet;
        long lastWarning;
        bool warned;

        public UdpOversizeLog(ActivityLog log) { this.log = log; }

        // fromInternet: resposta de um destino; senão, datagrama de um programa local para o relé.
        public void Dropped(bool fromInternet)
        {
            int programs, internet;
            lock (sync)
            {
                if (fromInternet) this.fromInternet++;
                else fromPrograms++;
                long now = Stopwatch.GetTimestamp();
                if (warned && now - lastWarning < Stopwatch.Frequency * MinIntervalSeconds) return;
                warned = true;
                lastWarning = now;
                programs = fromPrograms;
                internet = this.fromInternet;
                fromPrograms = this.fromInternet = 0;
            }
            log.Add(string.Format("UDP: {0} datagrama(s) maior(es) que {1} KB descartado(s) ({2} de programas deste computador, {3} da internet). " +
                                  "Se algum programa falhar em UDP pelo MultiLink, o limite precisa subir.",
                                  programs + internet, BufferPool.Datagram.Size / 1024, programs, internet), ActivityLog.Error);
        }
    }
}
