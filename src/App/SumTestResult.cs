// Resultado do teste da soma: quanto os links rendem baixando juntos, comparado ao melhor deles sozinho.
using System;
using System.Collections.Generic;
using System.Linq;
using MultiLink.Common;

namespace MultiLink.App
{
    public class SumTestResult
    {
        public DateTime At;
        public double TotalMbps;                                // todos os links juntos, no mesmo intervalo
        public string BestName;                                 // o link que mais rendeu no teste conjunto
        public double BestAloneMbps;                            // esse link medido sozinho, logo depois
        public List<KeyValuePair<string, double>> PerLink;      // nome e Mbps de cada link no teste conjunto

        // Quanto a soma rende a mais que o melhor link sozinho, em %; null se o link sozinho não pôde ser medido.
        public double? GainPct { get { return BestAloneMbps >= 1 ? (TotalMbps / BestAloneMbps - 1) * 100 : (double?)null; } }

        public string Summary()
        {
            var text = string.Format("Soma: {0:0} Mbps · melhor link sozinho ({1}): ", TotalMbps, BestName) +
                       (BestAloneMbps >= 1 ? string.Format("{0:0} Mbps", BestAloneMbps) : "não mediu");
            if (GainPct.HasValue) text += string.Format(" · ganho de {0:+0;-0;0}%", GainPct.Value);
            return text;
        }

        public object ToState()
        {
            return new Dictionary<string, object>
            {
                { "at", UnixTime.ToMs(At) }, { "totalMbps", Math.Round(TotalMbps) },
                { "bestName", BestName }, { "bestAloneMbps", Math.Round(BestAloneMbps) },
                { "gainPct", GainPct.HasValue ? (object)Math.Round(GainPct.Value) : null },
                { "perLink", PerLink.Select(pair => (object)new Dictionary<string, object> { { "name", pair.Key }, { "mbps", Math.Round(pair.Value) } }).ToList() }
            };
        }
    }
}
