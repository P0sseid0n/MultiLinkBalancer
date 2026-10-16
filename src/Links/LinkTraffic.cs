using System.Threading;

namespace MultiLink.Links
{
    // Contadores de tráfego de um link. As conexões os atualizam com Interlocked; a taxa por
    // segundo é calculada uma vez por segundo pelo AppController.
    public class LinkTraffic
    {
        public int ActiveConnections;
        public long TotalConnections;
        public long BytesUp;
        public long BytesDown;

        public double RateUp, RateDown; // bytes/s no último intervalo
        long lastBytesUp, lastBytesDown;

        // Atualiza as taxas e devolve os bytes trafegados desde a chamada anterior.
        public void UpdateRates(double seconds, out long downDelta, out long upDelta)
        {
            long up = Interlocked.Read(ref BytesUp), down = Interlocked.Read(ref BytesDown);
            upDelta = up - lastBytesUp;
            downDelta = down - lastBytesDown;
            RateUp = upDelta / seconds;
            RateDown = downDelta / seconds;
            lastBytesUp = up;
            lastBytesDown = down;
        }
    }
}
