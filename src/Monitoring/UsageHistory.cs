// Histórico de uso: velocidade, latência e perda de pacotes por link em pontos de 1 minuto (últimas 24 h),
// quedas (últimos 30 dias) e dados trafegados por dia (últimos 31 dias).
// Fica em %APPDATA%\MultiLinkBalancer\history.json, gravado a cada 5 min e ao sair.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MultiLink.Common;
using MultiLink.Links;
using MultiLink.Storage;

namespace MultiLink.Monitoring
{
    public class UsageHistory
    {
        static readonly string FilePath = Path.Combine(AppPaths.Roaming, "history.json");
        const long MinuteMs = 60000;
        const int MaxPoints = 24 * 60;
        const int MaxDays = 31, OutageDays = 30, MaxOutages = 500;
        static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

        // ---- formato do history.json: os nomes curtos dos campos são o formato do arquivo, não renomeie.

        // Ponto de 1 minuto. V: por id do link, [download B/s, upload B/s, latência média ms (-1 = sem medida), fração do minuto online,
        // perda de pacotes média % (-1 = sem medida; ausente nos pontos gravados antes de existir)]
        public class Point { public long T; public Dictionary<string, double[]> V = new Dictionary<string, double[]>(); }
        // Queda. End = 0: o link ainda está fora.
        public class Outage { public string Id, Name, Health; public long Start, End; }
        // Dia (D = "aaaa-mm-dd"). V: por id do link, [bytes baixados, bytes enviados]
        public class Day { public string D; public Dictionary<string, long[]> V = new Dictionary<string, long[]>(); }

        // soma das amostras de um link no minuto corrente
        class MinuteTotals { public long BytesDown, BytesUp; public double LatencySum, LossSum; public int LatencySamples, LossSamples, OnlineSamples, Samples; }

        readonly object sync = new object();
        readonly List<Point> points = new List<Point>();
        readonly List<Outage> outages = new List<Outage>();
        readonly List<Day> days = new List<Day>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>(); // id -> nome mais recente
        readonly Dictionary<string, MinuteTotals> currentMinute = new Dictionary<string, MinuteTotals>();
        long currentMinuteStart = MinuteStart(UnixTime.NowMs());
        DateTime lastSave = DateTime.UtcNow;

        static long MinuteStart(long timeMs) { return timeMs / MinuteMs * MinuteMs; }

        // Chamado a cada segundo para cada link, com os bytes trafegados desde a chamada anterior.
        public void Sample(Link link, long bytesDown, long bytesUp)
        {
            bytesDown = Math.Max(0, bytesDown);
            bytesUp = Math.Max(0, bytesUp);
            lock (sync)
            {
                names[link.Id] = link.Name;
                MinuteTotals totals;
                if (!currentMinute.TryGetValue(link.Id, out totals)) currentMinute[link.Id] = totals = new MinuteTotals();
                totals.BytesDown += bytesDown;
                totals.BytesUp += bytesUp;
                totals.Samples++;
                bool online = link.Present && link.InternetUp;
                if (online) totals.OnlineSamples++;
                if (online && link.LatencyMs >= 0) { totals.LatencySum += link.LatencyMs; totals.LatencySamples++; }
                int loss = link.Quality.LossPct;
                if (online && loss >= 0) { totals.LossSum += loss; totals.LossSamples++; }

                if (bytesDown > 0 || bytesUp > 0) AddToToday(link.Id, bytesDown, bytesUp);
            }
        }

        void AddToToday(string linkId, long bytesDown, long bytesUp)
        {
            var key = DateTime.Now.ToString("yyyy-MM-dd");
            var today = days.Count > 0 && days[days.Count - 1].D == key ? days[days.Count - 1] : null;
            if (today == null)
            {
                days.Add(today = new Day { D = key });
                if (days.Count > MaxDays) days.RemoveRange(0, days.Count - MaxDays);
            }
            long[] totals;
            if (!today.V.TryGetValue(linkId, out totals)) today.V[linkId] = totals = new long[2];
            totals[0] += bytesDown;
            totals[1] += bytesUp;
        }

        // Chamado a cada segundo depois dos Sample: fecha o minuto quando ele vira.
        public void Flush()
        {
            lock (sync)
            {
                long minute = MinuteStart(UnixTime.NowMs());
                if (minute == currentMinuteStart) return;
                var point = new Point { T = currentMinuteStart };
                foreach (var entry in currentMinute)
                {
                    var totals = entry.Value;
                    if (totals.Samples == 0) continue;
                    point.V[entry.Key] = new[]
                    {
                        Math.Round(totals.BytesDown / 60.0), Math.Round(totals.BytesUp / 60.0),
                        totals.LatencySamples > 0 ? Math.Round(totals.LatencySum / totals.LatencySamples, 1) : -1,
                        Math.Round(totals.OnlineSamples / (double)totals.Samples, 2),
                        totals.LossSamples > 0 ? Math.Round(totals.LossSum / totals.LossSamples, 1) : -1
                    };
                }
                currentMinute.Clear();
                currentMinuteStart = minute;
                if (point.V.Count > 0) points.Add(point);
                long cutoff = minute - MaxPoints * MinuteMs;
                points.RemoveAll(old => old.T < cutoff);
            }
            if (DateTime.UtcNow - lastSave >= SaveInterval) Save();
        }

        public void OutageStart(Link link, string health)
        {
            lock (sync)
            {
                if (outages.Any(outage => outage.Id == link.Id && outage.End == 0)) return;
                long now = UnixTime.NowMs();
                outages.Add(new Outage { Id = link.Id, Name = link.Name, Health = health, Start = now });
                long cutoff = now - OutageDays * 24L * 3600 * 1000;
                outages.RemoveAll(outage => outage.End != 0 && outage.End < cutoff);
                if (outages.Count > MaxOutages) outages.RemoveRange(0, outages.Count - MaxOutages);
            }
        }

        public void OutageEnd(Link link)
        {
            lock (sync)
                foreach (var outage in outages.Where(outage => outage.Id == link.Id && outage.End == 0))
                    outage.End = UnixTime.NowMs();
        }

        // Dados para a tela de histórico. minutes: janela dos gráficos (60, 360 ou 1440).
        public Dictionary<string, object> Get(int minutes, List<Link> links)
        {
            lock (sync)
            {
                foreach (var link in links) names[link.Id] = link.Name;
                long cutoff = MinuteStart(UnixTime.NowMs()) - minutes * MinuteMs;
                var recent = points.Where(point => point.T >= cutoff).ToList();
                // janelas longas: agrupa em blocos (6 h -> 5 min, 24 h -> 15 min) para o gráfico continuar legível
                int stepMinutes = minutes <= 60 ? 1 : minutes <= 360 ? 5 : 15;
                long stepMs = stepMinutes * MinuteMs;
                var series = stepMinutes == 1 ? recent
                    : recent.GroupBy(point => point.T / stepMs).Select(group => Average(group.Key * stepMs, group.ToList())).ToList();
                return new Dictionary<string, object>
                {
                    { "now", UnixTime.NowMs() }, { "minutes", minutes }, { "step", stepMinutes },
                    { "names", names.ToDictionary(pair => pair.Key, pair => (object)pair.Value) },
                    { "points", series.Select(PointToJson).ToList() },
                    { "outages", outages.OrderByDescending(outage => outage.Start).Take(100).Select(OutageToJson).ToList() },
                    { "days", days.OrderByDescending(day => day.D).Take(MaxDays).Select(DayToJson).ToList() }
                };
            }
        }

        object PointToJson(Point point)
        {
            return new Dictionary<string, object> { { "t", point.T }, { "v", point.V.ToDictionary(pair => pair.Key, pair => (object)pair.Value) } };
        }

        object OutageToJson(Outage outage)
        {
            return new Dictionary<string, object>
            {
                { "id", outage.Id }, { "name", names.ContainsKey(outage.Id) ? names[outage.Id] : outage.Name }, { "health", outage.Health },
                { "start", outage.Start }, { "end", outage.End == 0 ? null : (object)outage.End }
            };
        }

        static object DayToJson(Day day)
        {
            return new Dictionary<string, object> { { "day", day.D }, { "v", day.V.ToDictionary(pair => pair.Key, pair => (object)pair.Value) } };
        }

        // Média de vários pontos num só (latência e perda: só dos minutos que tiveram medida).
        static Point Average(long time, List<Point> group)
        {
            var merged = new Point { T = time };
            foreach (var linkId in group.SelectMany(point => point.V.Keys).Distinct())
            {
                var values = group.Where(point => point.V.ContainsKey(linkId)).Select(point => point.V[linkId]).ToList();
                var latencies = values.Where(value => value[2] >= 0).Select(value => value[2]).ToList();
                var losses = values.Where(value => value.Length > 4 && value[4] >= 0).Select(value => value[4]).ToList();
                merged.V[linkId] = new[]
                {
                    Math.Round(values.Average(value => value[0])), Math.Round(values.Average(value => value[1])),
                    latencies.Count > 0 ? Math.Round(latencies.Average(), 1) : -1, Math.Round(values.Average(value => value[3]), 2),
                    losses.Count > 0 ? Math.Round(losses.Average(), 1) : -1
                };
            }
            return merged;
        }

        // ------------------------------------------------------------ arquivo
        public void Save()
        {
            lastSave = DateTime.UtcNow;
            string json;
            lock (sync)
            {
                json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new Dictionary<string, object>
                {
                    { "names", names }, { "points", points }, { "outages", outages }, { "days", days }
                });
            }
            try
            {
                Directory.CreateDirectory(AppPaths.Roaming);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, json, Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
            }
            catch { }
        }

        public static UsageHistory Load()
        {
            var history = new UsageHistory();
            try
            {
                if (!File.Exists(FilePath)) return history;
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return history;
                object value;
                if (root.TryGetValue("names", out value) && value is Dictionary<string, object>)
                    foreach (var pair in (Dictionary<string, object>)value) history.names[pair.Key] = Convert.ToString(pair.Value);
                if (root.TryGetValue("points", out value)) history.points.AddRange(serializer.ConvertToType<List<Point>>(value));
                if (root.TryGetValue("outages", out value)) history.outages.AddRange(serializer.ConvertToType<List<Outage>>(value));
                if (root.TryGetValue("days", out value)) history.days.AddRange(serializer.ConvertToType<List<Day>>(value));
                // quedas abertas de uma sessão anterior: terminam onde o registro parou
                long lastRecorded = history.points.Count > 0 ? history.points[history.points.Count - 1].T + MinuteMs : UnixTime.NowMs();
                foreach (var outage in history.outages.Where(outage => outage.End == 0)) outage.End = Math.Max(outage.Start, lastRecorded);
                long cutoff = MinuteStart(UnixTime.NowMs()) - MaxPoints * MinuteMs;
                history.points.RemoveAll(point => point.T < cutoff);
            }
            catch { }
            return history;
        }
    }
}
