// Histórico de uso: velocidade e latência por link em pontos de 1 minuto (últimas 24 h),
// quedas (últimos 30 dias) e dados trafegados por dia (últimos 31 dias).
// Fica em %APPDATA%\MultiLinkBalancer\history.json, gravado a cada 5 min e ao sair.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MultiLink
{
    public class History
    {
        static readonly string FilePath = Path.Combine(Config.Dir, "history.json");
        const int MaxPoints = 24 * 60;

        // ponto de 1 minuto: por link [download B/s, upload B/s, latência média ms (-1 = sem medida), fração do minuto online]
        public class Point { public long T; public Dictionary<string, double[]> V = new Dictionary<string, double[]>(); }
        public class Outage { public string Id, Name, Health; public long Start, End; } // End = 0: ainda fora
        public class Day { public string D; public Dictionary<string, long[]> V = new Dictionary<string, long[]>(); } // por link [baixado, enviado]

        class Acc { public long Down, Up; public double LatSum; public int LatN, Ok, N; }

        readonly object sync = new object();
        readonly List<Point> points = new List<Point>();
        readonly List<Outage> outages = new List<Outage>();
        readonly List<Day> days = new List<Day>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly Dictionary<string, Acc> acc = new Dictionary<string, Acc>();
        long minute = CurrentMinute();
        DateTime lastSave = DateTime.UtcNow;

        public static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        static long CurrentMinute() { return Now() / 60000 * 60000; }

        // Chamado a cada segundo para cada link, com os bytes trafegados desde a chamada anterior.
        public void Sample(Link l, long down, long up)
        {
            lock (sync)
            {
                names[l.Id] = l.Name;
                Acc a;
                if (!acc.TryGetValue(l.Id, out a)) acc[l.Id] = a = new Acc();
                a.Down += Math.Max(0, down); a.Up += Math.Max(0, up); a.N++;
                bool ok = l.Present && l.Up;
                if (ok) a.Ok++;
                if (ok && l.LatencyMs >= 0) { a.LatSum += l.LatencyMs; a.LatN++; }

                if (down > 0 || up > 0)
                {
                    var key = DateTime.Now.ToString("yyyy-MM-dd");
                    var day = days.Count > 0 && days[days.Count - 1].D == key ? days[days.Count - 1] : null;
                    if (day == null)
                    {
                        days.Add(day = new Day { D = key });
                        if (days.Count > 31) days.RemoveRange(0, days.Count - 31);
                    }
                    long[] v;
                    if (!day.V.TryGetValue(l.Id, out v)) day.V[l.Id] = v = new long[2];
                    v[0] += Math.Max(0, down); v[1] += Math.Max(0, up);
                }
            }
        }

        // Chamado a cada segundo depois dos Sample: fecha o minuto quando ele vira.
        public void Flush()
        {
            lock (sync)
            {
                long m = CurrentMinute();
                if (m == minute) return;
                var p = new Point { T = minute };
                foreach (var e in acc)
                {
                    var a = e.Value;
                    if (a.N == 0) continue;
                    p.V[e.Key] = new[] { Math.Round(a.Down / 60.0), Math.Round(a.Up / 60.0),
                                         a.LatN > 0 ? Math.Round(a.LatSum / a.LatN, 1) : -1, Math.Round(a.Ok / (double)a.N, 2) };
                }
                acc.Clear();
                minute = m;
                if (p.V.Count > 0) points.Add(p);
                long cut = m - MaxPoints * 60000L;
                points.RemoveAll(x => x.T < cut);
            }
            if ((DateTime.UtcNow - lastSave).TotalMinutes >= 5) Save();
        }

        public void OutageStart(Link l, string health)
        {
            lock (sync)
            {
                if (outages.Any(o => o.Id == l.Id && o.End == 0)) return;
                outages.Add(new Outage { Id = l.Id, Name = l.Name, Health = health, Start = Now() });
                long cut = Now() - 30L * 24 * 3600 * 1000;
                outages.RemoveAll(o => o.End != 0 && o.End < cut);
                if (outages.Count > 500) outages.RemoveRange(0, outages.Count - 500);
            }
        }

        public void OutageEnd(Link l)
        {
            lock (sync) foreach (var o in outages.Where(o => o.Id == l.Id && o.End == 0)) o.End = Now();
        }

        // Dados para a tela de histórico. minutes: janela dos gráficos (60, 360 ou 1440).
        public Dictionary<string, object> Get(int minutes, List<Link> links)
        {
            lock (sync)
            {
                foreach (var l in links) names[l.Id] = l.Name;
                long cut = CurrentMinute() - minutes * 60000L;
                var pts = points.Where(p => p.T >= cut).ToList();
                // janelas longas: agrupa em blocos (6 h -> 5 min, 24 h -> 15 min) para o gráfico continuar legível
                int step = minutes <= 60 ? 1 : minutes <= 360 ? 5 : 15;
                var series = step == 1 ? pts : pts.GroupBy(p => p.T / (step * 60000L)).Select(g => Merge(g.Key * step * 60000L, g.ToList())).ToList();
                return new Dictionary<string, object>
                {
                    { "now", Now() }, { "minutes", minutes }, { "step", step },
                    { "names", names.ToDictionary(p => p.Key, p => (object)p.Value) },
                    { "points", series.Select(p => (object)new Dictionary<string, object> { { "t", p.T }, { "v", p.V.ToDictionary(x => x.Key, x => (object)x.Value) } }).ToList() },
                    { "outages", outages.OrderByDescending(o => o.Start).Take(100).Select(o => (object)new Dictionary<string, object> {
                        { "id", o.Id }, { "name", names.ContainsKey(o.Id) ? names[o.Id] : o.Name }, { "health", o.Health }, { "start", o.Start }, { "end", o.End == 0 ? null : (object)o.End } }).ToList() },
                    { "days", days.OrderByDescending(d => d.D).Take(31).Select(d => (object)new Dictionary<string, object> {
                        { "day", d.D }, { "v", d.V.ToDictionary(x => x.Key, x => (object)x.Value) } }).ToList() }
                };
            }
        }

        static Point Merge(long t, List<Point> g)
        {
            var p = new Point { T = t };
            foreach (var id in g.SelectMany(x => x.V.Keys).Distinct())
            {
                var vs = g.Where(x => x.V.ContainsKey(id)).Select(x => x.V[id]).ToList();
                var lat = vs.Where(v => v[2] >= 0).Select(v => v[2]).ToList();
                p.V[id] = new[] { Math.Round(vs.Average(v => v[0])), Math.Round(vs.Average(v => v[1])),
                                  lat.Count > 0 ? Math.Round(lat.Average(), 1) : -1, Math.Round(vs.Average(v => v[3]), 2) };
            }
            return p;
        }

        // ---------------------------------------------------------------- arquivo
        public void Save()
        {
            lastSave = DateTime.UtcNow;
            string text;
            lock (sync)
            {
                text = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new Dictionary<string, object>
                {
                    { "names", names }, { "points", points }, { "outages", outages }, { "days", days }
                });
            }
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, text, Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch { }
        }

        public static History Load()
        {
            var h = new History();
            try
            {
                if (!File.Exists(FilePath)) return h;
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return h;
                object o;
                if (root.TryGetValue("names", out o) && o is Dictionary<string, object>)
                    foreach (var p in (Dictionary<string, object>)o) h.names[p.Key] = Convert.ToString(p.Value);
                if (root.TryGetValue("points", out o)) h.points.AddRange(js.ConvertToType<List<Point>>(o));
                if (root.TryGetValue("outages", out o)) h.outages.AddRange(js.ConvertToType<List<Outage>>(o));
                if (root.TryGetValue("days", out o)) h.days.AddRange(js.ConvertToType<List<Day>>(o));
                // quedas abertas de uma sessão anterior: terminam onde o registro parou
                long last = h.points.Count > 0 ? h.points[h.points.Count - 1].T + 60000 : Now();
                foreach (var x in h.outages.Where(x => x.End == 0)) x.End = Math.Max(x.Start, last);
                long cut = CurrentMinute() - MaxPoints * 60000L;
                h.points.RemoveAll(x => x.T < cut);
            }
            catch { }
            return h;
        }
    }
}
