// Pesos automáticos: cada link pesa o quanto já mostrou que rende, pelo tráfego que passa pelo MultiLink, sem teste
// de velocidade. Mesmo cálculo do net-merge (internal/engine/sched.go):
// - o pico de cada link é a maior vazão observada (descida + subida); só cai, 3% por segundo, quando o link está em
//   uso e rendendo menos que ele. Parado, o pico é lembrado.
// - um link parado (que talvez tenha sido medido num momento ruim e depois ficou sem conexões para se corrigir) tem
//   o pico subindo 3% por segundo, até metade do melhor link, para voltar a receber conexões e ser medido de novo.
// - enquanto algum link ainda não foi medido (nunca passou de ~0,4 Mbps), os pesos ficam iguais: assim todos
//   recebem conexões e são medidos. Depois, peso proporcional ao pico, com mínimo de 5% do melhor link.
using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiLink.Links
{
    public static class AutoWeights
    {
        public const int Equal = 100;            // peso do melhor link (e de todos, enquanto falta medir algum)
        const double MinShare = 0.05;
        const double MeasuredBytesPerSecond = 50000;
        const double ActiveShareOfPeak = 0.05;   // abaixo disso do pico, o link conta como parado
        const double DecayPerSecond = 0.97;
        const double RecoverPerSecond = 1.03;
        const double RecoverUpToShareOfBest = 0.5;

        // Chamado a cada segundo, depois de LinkTraffic.UpdateRates.
        public static void Update(List<Link> links)
        {
            var idle = new List<Link>();
            foreach (var link in links.Where(link => link.Present))
            {
                double rate = link.Traffic.RateDown + link.Traffic.RateUp;
                if (rate >= ActiveShareOfPeak * link.PeakBytesPerSecond)
                    link.PeakBytesPerSecond = Math.Max(rate, link.PeakBytesPerSecond * DecayPerSecond);
                else idle.Add(link);
            }

            var usable = links.Where(link => link.Present && link.Enabled && link.InternetUp).ToList();
            double best = usable.Count == 0 ? 0 : usable.Max(link => link.PeakBytesPerSecond);
            foreach (var link in idle.Where(link => usable.Contains(link) && link.PeakBytesPerSecond > 0))
                link.PeakBytesPerSecond = Math.Min(link.PeakBytesPerSecond * RecoverPerSecond,
                                                   Math.Max(link.PeakBytesPerSecond, RecoverUpToShareOfBest * best));

            bool allMeasured = usable.All(link => link.PeakBytesPerSecond >= MeasuredBytesPerSecond);
            foreach (var link in usable)
                link.AutoWeight = usable.Count < 2 || !allMeasured ? Equal
                    : (int)Math.Round(Equal * Math.Max(MinShare, link.PeakBytesPerSecond / best));
        }
    }
}
