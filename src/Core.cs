// MultiLink Balancer - balanceamento de carga entre múltiplas conexões de rede no Windows.
// Funciona como proxy local SOCKS4/SOCKS5/HTTP: cada nova conexão TCP sai por um adaptador
// diferente (socket vinculado ao IP de origem do adaptador), com health check e failover.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;


namespace MultiLink
{
    public enum BalanceMode { WeightedRoundRobin = 0, LeastConnections = 1, Failover = 2, LowestLatency = 3 }

    public class Link
    {
        public string Id;
        public string Name;
        public string Description;
        public IPAddress Address;
        public IPAddress Address6; // IPv6 global, se houver
        public string Gateway;
        public long SpeedBps;
        public bool Wired;
        public volatile bool Present = true;
        public volatile bool Enabled = true;
        public volatile int Weight = 1;
        public volatile bool Up = true;
        public volatile int LatencyMs = -1;
        public double LatencyAvg = -1;             // média móvel da latência (modo "menor latência")
        // qualidade: uma amostra a cada verificação (~3 s); perda e jitter das últimas 20 (~1 min).
        // A amostra é um ping (ICMP); se a rede bloqueia ICMP, vale o tempo da conexão TCP de teste.
        public readonly Queue<int> PingWindow = new Queue<int>(); // tempo de resposta em ms, -1 = perdido
        public bool IcmpOk, IcmpBlocked;           // ICMP já respondeu / nunca respondeu em várias tentativas
        public int IcmpMisses;
        public volatile int LossPct = -1;          // -1 = sem dados suficientes
        public volatile int JitterMs = -1;
        public bool Unstable;                      // último aviso de instabilidade dado no registro de eventos
        public int ConsecutiveFailures;
        public int Active;
        public long Total;
        public long Failed;
        public long BytesUp;
        public long BytesDown;
        public int CurrentWeight;
        public double MeasuredMbps = -1;
        // monitoramento de queda
        public volatile string AdapterState;       // quando ausente: "unplugged", "disabled" ou "nocfg"
        public volatile bool GatewayReachable = true;
        public volatile bool UserDisabled;         // desativado pelo próprio app: não alertar
        public string Health;                      // último estado avaliado pela UI
        public DateTime DownSince;
        public volatile bool MetricRaised, MetricBusy;
        // usados pela UI para calcular taxa
        public long LastUp, LastDown;
        public double RateUp, RateDown;

        // 1 = sem perda; cai rápido com a perda de pacotes (10% -> 0,67; 20% -> 0,33; 30% ou mais -> 0,05)
        public double Quality
        {
            get { int loss = LossPct; return loss <= 0 ? 1 : Math.Max(0.05, 1 - loss / 30.0); }
        }

        public void AddPing(int rttMs)
        {
            lock (PingWindow)
            {
                PingWindow.Enqueue(rttMs);
                while (PingWindow.Count > 20) PingWindow.Dequeue();
                var w = PingWindow.ToArray();
                if (w.Length < 5) { LossPct = -1; JitterMs = -1; return; }
                LossPct = (int)Math.Round(100.0 * w.Count(x => x < 0) / w.Length);
                // jitter: variação média entre respostas seguidas (como no RTP, RFC 3550)
                var ok = w.Where(x => x >= 0).ToArray();
                double sum = 0;
                for (int i = 1; i < ok.Length; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
                JitterMs = ok.Length > 1 ? (int)Math.Round(sum / (ok.Length - 1)) : -1;
            }
        }

        public void ResetPings()
        {
            lock (PingWindow) { PingWindow.Clear(); LossPct = -1; JitterMs = -1; IcmpOk = IcmpBlocked = false; IcmpMisses = 0; }
        }
    }

    // ------------------------------------------------------------------ Balanceador
    public class Balancer
    {
        readonly object sync = new object();
        readonly Dictionary<string, KeyValuePair<Link, DateTime>> affinity =
            new Dictionary<string, KeyValuePair<Link, DateTime>>(StringComparer.OrdinalIgnoreCase);

        public readonly List<Link> Links = new List<Link>();
        public BalanceMode Mode = BalanceMode.WeightedRoundRobin;
        public bool Affinity = false;
        public bool QualityAware = true; // perda de pacotes reduz o peso; perda e jitter pesam no modo "menor latência"
        Link lowLatency;

        // peso efetivo em décimos, para a redução pela qualidade não sumir no arredondamento de pesos pequenos
        int Eff(Link l)
        {
            if (l.Weight <= 0) return 0;
            return QualityAware ? Math.Max(1, (int)Math.Round(l.Weight * 10 * l.Quality)) : l.Weight * 10;
        }

        // nota para o modo "menor latência": ms equivalentes (cada 1% de perda conta como 10 ms)
        double Score(Link l)
        {
            if (l.LatencyAvg < 0) return double.MaxValue;
            if (!QualityAware) return l.LatencyAvg;
            return l.LatencyAvg + Math.Max(0, l.JitterMs) + Math.Max(0, l.LossPct) * 10;
        }
        public TimeSpan AffinityTtl = TimeSpan.FromMinutes(10);

        public List<Link> Snapshot()
        {
            lock (sync) return Links.ToList();
        }

        public void Mutate(Action<List<Link>> action)
        {
            lock (sync) action(Links);
        }

        public Link Pick(string host, ICollection<Link> exclude)
        {
            lock (sync)
            {
                var c = Links.Where(l => l.Enabled && l.Present && l.Up && l.Weight > 0 && !exclude.Contains(l)).ToList();
                if (c.Count == 0) // health check pode dar falso negativo: tenta qualquer link ativo
                    c = Links.Where(l => l.Enabled && l.Present && l.Weight > 0 && !exclude.Contains(l)).ToList();
                if (c.Count == 0) return null;

                string key = Affinity ? AffinityKey(host) : null;
                if (key != null && exclude.Count == 0)
                {
                    KeyValuePair<Link, DateTime> e;
                    if (affinity.TryGetValue(key, out e) && e.Value > DateTime.UtcNow && c.Contains(e.Key))
                    {
                        affinity[key] = new KeyValuePair<Link, DateTime>(e.Key, DateTime.UtcNow + AffinityTtl);
                        return e.Key;
                    }
                }

                Link chosen;
                switch (Mode)
                {
                    case BalanceMode.LeastConnections:
                        chosen = c.OrderBy(l => (Thread.VolatileRead(ref l.Active) + 1) / (double)Eff(l))
                                  .ThenBy(l => Interlocked.Read(ref l.Total)).First();
                        break;
                    case BalanceMode.Failover:
                        chosen = c.OrderByDescending(l => Eff(l)).First();
                        break;
                    case BalanceMode.LowestLatency:
                        // o link atual só perde a vez se outro for claramente melhor (evita trocar a cada oscilação)
                        chosen = c.OrderBy(l => Score(l)).ThenByDescending(l => l.Weight).First();
                        if (lowLatency != null && c.Contains(lowLatency) && lowLatency.LatencyAvg >= 0 &&
                            Score(lowLatency) <= Score(chosen) * 1.2 + 3) chosen = lowLatency;
                        lowLatency = chosen;
                        break;
                    default: // smooth weighted round-robin (algoritmo do nginx)
                        int total = 0; chosen = null;
                        foreach (var l in c)
                        {
                            int w = Eff(l);
                            l.CurrentWeight += w;
                            total += w;
                            if (chosen == null || l.CurrentWeight > chosen.CurrentWeight) chosen = l;
                        }
                        chosen.CurrentWeight -= total;
                        break;
                }

                if (key != null && Mode != BalanceMode.Failover && Mode != BalanceMode.LowestLatency)
                {
                    if (affinity.Count > 20000)
                    {
                        var now = DateTime.UtcNow;
                        foreach (var k in affinity.Where(p => p.Value.Value < now).Select(p => p.Key).ToList()) affinity.Remove(k);
                    }
                    affinity[key] = new KeyValuePair<Link, DateTime>(chosen, DateTime.UtcNow + AffinityTtl);
                }
                return chosen;
            }
        }

        // Agrupa subdomínios (www.google.com, mail.google.com -> google.com) para manter sessões no mesmo IP.
        static string AffinityKey(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return host;
            var p = host.TrimEnd('.').Split('.');
            if (p.Length <= 2) return host;
            int take = (p[p.Length - 1].Length == 2 && p[p.Length - 2].Length <= 3) ? 3 : 2; // ex.: .com.br, .co.uk
            return string.Join(".", p.Skip(Math.Max(0, p.Length - take)));
        }
    }

    // ------------------------------------------------------------------ Proxy
    class Target
    {
        public string Host;
        public int Port;
        public byte[] Initial;
        public Func<Task> OnSuccess;
        public Func<Task> OnFail;
        public bool Udp; // SOCKS5 UDP ASSOCIATE: a conexão TCP só mantém a associação viva
    }

    public class ProxyServer
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        readonly Balancer bal;
        readonly Action<string> log;
        readonly ConcurrentDictionary<Socket, byte> open = new ConcurrentDictionary<Socket, byte>();
        TcpListener listener;
        CancellationTokenSource cts;

        public ProxyServer(Balancer bal, Action<string> log) { this.bal = bal; this.log = log; }
        public bool Running { get { return listener != null; } }
        public int OpenSockets { get { return open.Count; } }

        public void Start(int port, bool allowLan)
        {
            var l = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
            l.Start(1024);
            listener = l;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            Task.Run(() => AcceptLoop(l, ct));
        }

        public void Stop()
        {
            if (listener == null) return;
            cts.Cancel();
            try { listener.Stop(); } catch { }
            listener = null;
            foreach (var s in open.Keys.ToList()) Close(s);
        }

        async Task AcceptLoop(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); }
                catch { if (ct.IsCancellationRequested) break; continue; }
                var ignored = Handle(c);
            }
        }

        void Close(Socket s)
        {
            if (s == null) return;
            byte b; open.TryRemove(s, out b);
            try { s.Close(); } catch { }
        }

        async Task Handle(TcpClient client)
        {
            Socket cs = client.Client, remote = null;
            open[cs] = 0;
            try
            {
                cs.NoDelay = true;
                var stream = new NetworkStream(cs, false);
                var first = new byte[1];
                if (!await ReadExact(stream, first, 1)) return;

                Target t;
                if (first[0] == 5) t = await Socks5(stream);
                else if (first[0] == 4) t = await Socks4(stream);
                else t = await Http(stream, first[0]);
                if (t == null) return;
                if (t.Udp) { await UdpAssociate(stream, cs); return; }

                var res = await ConnectBalanced(t.Host, t.Port);
                if (res == null) { await t.OnFail(); return; }
                remote = res.Item1;
                var link = res.Item2;
                open[remote] = 0;

                var rstream = new NetworkStream(remote, false);
                await t.OnSuccess();
                if (t.Initial != null && t.Initial.Length > 0)
                {
                    await rstream.WriteAsync(t.Initial, 0, t.Initial.Length);
                    Interlocked.Add(ref link.BytesUp, t.Initial.Length);
                }

                Interlocked.Increment(ref link.Active);
                try
                {
                    var up = Copy(stream, rstream, remote, n => Interlocked.Add(ref link.BytesUp, n));
                    var down = Copy(rstream, stream, cs, n => Interlocked.Add(ref link.BytesDown, n));
                    await Task.WhenAny(up, down);
                    await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(30000));
                }
                finally { Interlocked.Decrement(ref link.Active); }
            }
            catch { }
            finally
            {
                Close(remote);
                Close(cs);
            }
        }

        static async Task Copy(Stream src, Stream dst, Socket dstSock, Action<int> count)
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    count(n);
                }
            }
            catch { }
            try { dstSock.Shutdown(SocketShutdown.Send); } catch { }
        }

        async Task<Tuple<Socket, Link>> ConnectBalanced(string host, int port)
        {
            IPAddress[] addrs;
            IPAddress literal;
            try
            {
                if (IPAddress.TryParse(host, out literal)) addrs = new[] { literal };
                else addrs = await Dns.GetHostAddressesAsync(host);
            }
            catch (Exception ex) { log("DNS falhou para " + host + ": " + ex.Message); return null; }

            // IPv4 é o padrão; IPv6 só para destinos sem IPv4, e apenas em links que tenham IPv6
            var targets = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2).ToArray();
            bool v6 = targets.Length == 0;
            if (v6) targets = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(2).ToArray();
            if (targets.Length == 0) { log("Nenhum endereço para " + host); return null; }

            var tried = new List<Link>();
            if (v6) tried.AddRange(bal.Snapshot().Where(l => l.Address6 == null));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var link = bal.Pick(host, tried);
                if (link == null) { if (v6 && attempt == 0) log("Nenhum link com IPv6 para " + host); break; }
                tried.Add(link);
                string err = null;
                foreach (var a in targets)
                {
                    var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        s.Bind(new IPEndPoint(v6 ? link.Address6 : link.Address, 0));
                        s.NoDelay = true;
                        var ct = Task.Factory.FromAsync<EndPoint>(s.BeginConnect, s.EndConnect, new IPEndPoint(a, port), null);
                        if (await Task.WhenAny(ct, Task.Delay(8000)) != ct)
                        {
                            ct.ContinueWith(x => { var ignored = x.Exception; });
                            throw new TimeoutException("tempo esgotado");
                        }
                        await ct;
                        Interlocked.Increment(ref link.Total);
                        return Tuple.Create(s, link);
                    }
                    catch (Exception ex) { err = ex.Message; try { s.Close(); } catch { } }
                }
                Interlocked.Increment(ref link.Failed);
                log(string.Format("[{0}] falha ao conectar {1}:{2} ({3}){4}", link.Name, host, port, err,
                    attempt < 2 ? " - tentando outro link" : ""));
            }
            return null;
        }

        // ---- SOCKS5 (RFC 1928), apenas CONNECT, sem autenticação
        async Task<Target> Socks5(NetworkStream s)
        {
            var b = new byte[1];
            if (!await ReadExact(s, b, 1)) return null;
            var methods = new byte[b[0]];
            if (!await ReadExact(s, methods, methods.Length)) return null;
            if (!methods.Contains((byte)0)) { await Write(s, new byte[] { 5, 0xFF }); return null; }
            await Write(s, new byte[] { 5, 0 });

            var req = new byte[4];
            if (!await ReadExact(s, req, 4)) return null;
            string host;
            switch (req[3])
            {
                case 1: { var a = new byte[4]; if (!await ReadExact(s, a, 4)) return null; host = new IPAddress(a).ToString(); break; }
                case 4: { var a = new byte[16]; if (!await ReadExact(s, a, 16)) return null; host = new IPAddress(a).ToString(); break; }
                case 3:
                    {
                        if (!await ReadExact(s, b, 1)) return null;
                        var d = new byte[b[0]]; if (!await ReadExact(s, d, d.Length)) return null;
                        host = Encoding.ASCII.GetString(d); break;
                    }
                default: return null;
            }
            var pb = new byte[2];
            if (!await ReadExact(s, pb, 2)) return null;
            int port = (pb[0] << 8) | pb[1];
            if (req[1] == 3) return new Target { Host = host, Port = port, Udp = true };
            if (req[1] != 1) { await Write(s, new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }); return null; }

            return new Target
            {
                Host = host, Port = port,
                OnSuccess = () => Write(s, new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }),
                OnFail = () => Write(s, new byte[] { 5, 4, 0, 1, 0, 0, 0, 0, 0, 0 })
            };
        }

        // ---- SOCKS5 UDP ASSOCIATE (RFC 1928, seção 7)
        // O cliente manda datagramas com um cabeçalho SOCKS para o relé local. Cada destino novo (fluxo)
        // ganha um socket UDP preso ao IP de um link escolhido pelo balanceador; as respostas voltam
        // com o cabeçalho do remetente. A associação dura enquanto a conexão TCP de controle estiver aberta.
        class UdpFlow { public Socket Sock; public Link Link; public long Last; }

        async Task UdpAssociate(NetworkStream control, Socket cs)
        {
            var clientIp = ((IPEndPoint)cs.RemoteEndPoint).Address;
            var local = ((IPEndPoint)cs.LocalEndPoint).Address;
            var relay = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var flows = new ConcurrentDictionary<string, UdpFlow>();
            EndPoint client = null;
            try
            {
                IgnoreConnReset(relay);
                relay.Bind(new IPEndPoint(local, 0));
                var bnd = (IPEndPoint)relay.LocalEndPoint;
                var a = bnd.Address.MapToIPv4().GetAddressBytes();
                await Write(control, new byte[] { 5, 0, 0, 1, a[0], a[1], a[2], a[3], (byte)(bnd.Port >> 8), (byte)bnd.Port });

                var done = new TaskCompletionSource<bool>();
                // fim da associação: o cliente fecha a conexão de controle
                var watch = Task.Run(async () => { var b = new byte[64]; try { while (await control.ReadAsync(b, 0, b.Length) > 0) { } } catch { } done.TrySetResult(true); });
                var idle = Task.Run(async () =>
                {
                    while (!done.Task.IsCompleted)
                    {
                        await Task.WhenAny(done.Task, Task.Delay(15000));
                        long cut = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 120;
                        foreach (var p in flows.Where(p => Interlocked.Read(ref p.Value.Last) < cut).ToList())
                        {
                            UdpFlow f;
                            if (flows.TryRemove(p.Key, out f)) CloseFlow(f);
                        }
                    }
                });

                var buf = new byte[65536];
                while (!done.Task.IsCompleted)
                {
                    EndPoint from = new IPEndPoint(local.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    var recv = Task.Factory.FromAsync((cb, st) => relay.BeginReceiveFrom(buf, 0, buf.Length, SocketFlags.None, ref from, cb, st),
                                                      ar => relay.EndReceiveFrom(ar, ref from), null);
                    if (await Task.WhenAny(recv, done.Task) != recv) { recv.ContinueWith(x => { var e = x.Exception; }); break; }
                    int n;
                    try { n = await recv; } catch (SocketException) { continue; }
                    var src = (IPEndPoint)from;
                    // só o dono da associação pode usar o relé
                    if (!src.Address.Equals(clientIp) && !(IPAddress.IsLoopback(src.Address) && IPAddress.IsLoopback(clientIp))) continue;
                    client = from;
                    await RelayOut(buf, n, flows, relay, () => client);
                }
            }
            catch { }
            finally
            {
                try { relay.Close(); } catch { }
                foreach (var f in flows.Values) CloseFlow(f);
            }
        }

        // Sem isso, um ICMP "porta inacessível" faz a próxima leitura do socket UDP falhar (SIO_UDP_CONNRESET).
        static void IgnoreConnReset(Socket s)
        {
            try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch { }
        }

        static void CloseFlow(UdpFlow f)
        {
            try { f.Sock.Close(); } catch { }
            Interlocked.Decrement(ref f.Link.Active);
        }

        async Task RelayOut(byte[] buf, int n, ConcurrentDictionary<string, UdpFlow> flows, Socket relay, Func<EndPoint> client)
        {
            // RSV(2) FRAG(1) ATYP(1) DST.ADDR DST.PORT DADOS; fragmentação não é suportada (FRAG != 0 é descartado)
            if (n < 10 || buf[2] != 0) return;
            int off; IPAddress dst;
            switch (buf[3])
            {
                case 1: dst = new IPAddress(new[] { buf[4], buf[5], buf[6], buf[7] }); off = 8; break;
                case 4:
                    if (n < 22) return;
                    var b6 = new byte[16]; Buffer.BlockCopy(buf, 4, b6, 0, 16); dst = new IPAddress(b6); off = 20; break;
                case 3:
                    int len = buf[4]; if (n < 7 + len) return;
                    var name = Encoding.ASCII.GetString(buf, 5, len); off = 5 + len;
                    try { dst = (await Dns.GetHostAddressesAsync(name)).OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First(); }
                    catch { return; }
                    break;
                default: return;
            }
            int port = (buf[off] << 8) | buf[off + 1];
            off += 2;
            var ep = new IPEndPoint(dst, port);
            var key = ep.ToString();
            UdpFlow flow;
            if (!flows.TryGetValue(key, out flow))
            {
                bool v6 = dst.AddressFamily == AddressFamily.InterNetworkV6;
                var exclude = new List<Link>();
                if (v6) exclude.AddRange(bal.Snapshot().Where(l => l.Address6 == null));
                var link = bal.Pick(dst.ToString(), exclude);
                if (link == null) return;
                var s = new Socket(dst.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                try { IgnoreConnReset(s); s.Bind(new IPEndPoint(v6 ? link.Address6 : link.Address, 0)); }
                catch { s.Close(); return; }
                flow = new UdpFlow { Sock = s, Link = link, Last = Stopwatch.GetTimestamp() };
                if (!flows.TryAdd(key, flow)) { s.Close(); flow = flows[key]; }
                else
                {
                    Interlocked.Increment(ref link.Active);
                    Interlocked.Increment(ref link.Total);
                    var ignored = RelayBack(flow, dst.AddressFamily, relay, client);
                }
            }
            Interlocked.Exchange(ref flow.Last, Stopwatch.GetTimestamp());
            try { Interlocked.Add(ref flow.Link.BytesUp, flow.Sock.SendTo(buf, off, n - off, SocketFlags.None, ep)); }
            catch { }
        }

        // Respostas do destino: devolve ao cliente com o cabeçalho SOCKS do remetente.
        static async Task RelayBack(UdpFlow flow, AddressFamily af, Socket relay, Func<EndPoint> client)
        {
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    EndPoint from = new IPEndPoint(af == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int n = await Task.Factory.FromAsync((cb, st) => flow.Sock.BeginReceiveFrom(buf, 0, buf.Length, SocketFlags.None, ref from, cb, st),
                                                         ar => flow.Sock.EndReceiveFrom(ar, ref from), null);
                    Interlocked.Add(ref flow.Link.BytesDown, n);
                    Interlocked.Exchange(ref flow.Last, Stopwatch.GetTimestamp());
                    var src = (IPEndPoint)from;
                    var ab = src.Address.GetAddressBytes();
                    var pkt = new byte[6 + ab.Length + n];
                    pkt[3] = (byte)(ab.Length == 4 ? 1 : 4);
                    Buffer.BlockCopy(ab, 0, pkt, 4, ab.Length);
                    pkt[4 + ab.Length] = (byte)(src.Port >> 8);
                    pkt[5 + ab.Length] = (byte)src.Port;
                    Buffer.BlockCopy(buf, 0, pkt, 6 + ab.Length, n);
                    var c = client();
                    if (c != null) relay.SendTo(pkt, c);
                }
            }
            catch { } // socket fechado: associação encerrada ou fluxo ocioso
        }

        // ---- SOCKS4 / SOCKS4a
        async Task<Target> Socks4(NetworkStream s)
        {
            var h = new byte[7];
            if (!await ReadExact(s, h, 7)) return null;
            int port = (h[1] << 8) | h[2];
            await ReadZString(s); // user id
            string host;
            if (h[3] == 0 && h[4] == 0 && h[5] == 0 && h[6] != 0) host = await ReadZString(s);
            else host = new IPAddress(new[] { h[3], h[4], h[5], h[6] }).ToString();
            if (host == null) return null;
            if (h[0] != 1) { await Write(s, new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 }); return null; }
            return new Target
            {
                Host = host, Port = port,
                OnSuccess = () => Write(s, new byte[] { 0, 0x5A, h[1], h[2], h[3], h[4], h[5], h[6] }),
                OnFail = () => Write(s, new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 })
            };
        }

        // ---- HTTP proxy (CONNECT para HTTPS e GET/POST absolutos para HTTP)
        async Task<Target> Http(NetworkStream s, byte first)
        {
            var buf = new byte[65536];
            buf[0] = first;
            int len = 1, end;
            while ((end = FindHeaderEnd(buf, len)) < 0)
            {
                if (len == buf.Length) return null;
                int n = await s.ReadAsync(buf, len, buf.Length - len);
                if (n <= 0) return null;
                len += n;
            }
            string header = Latin1.GetString(buf, 0, end);
            var leftover = new byte[len - end - 4];
            Buffer.BlockCopy(buf, end + 4, leftover, 0, leftover.Length);

            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length < 3) return null;
            string method = parts[0], target = parts[1], version = parts[2];

            var t = new Target
            {
                OnFail = () => Write(s, Encoding.ASCII.GetBytes(
                    "HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\nMultiLink: nenhum link conseguiu conectar ao destino.\r\n"))
            };

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!SplitHostPort(target, 443, out t.Host, out t.Port)) return null;
                t.Initial = leftover;
                t.OnSuccess = () => Write(s, Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
                return t;
            }

            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp)
            {
                await Write(s, Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n" +
                    "<html><body style='font-family:Segoe UI;padding:2em'><h2>MultiLink Balancer está ativo</h2>" +
                    "<p>Configure este endereço como proxy SOCKS5 ou HTTP nos seus aplicativos.</p></body></html>"));
                return null;
            }

            t.Host = uri.DnsSafeHost;
            t.Port = uri.Port;
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(version).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0)
                {
                    var name = lines[i].Substring(0, c).Trim().ToLowerInvariant();
                    if (name == "proxy-connection" || name == "proxy-authorization" || name == "connection" || name == "keep-alive") continue;
                }
                sb.Append(lines[i]).Append("\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");
            var hb = Latin1.GetBytes(sb.ToString());
            t.Initial = new byte[hb.Length + leftover.Length];
            Buffer.BlockCopy(hb, 0, t.Initial, 0, hb.Length);
            Buffer.BlockCopy(leftover, 0, t.Initial, hb.Length, leftover.Length);
            t.OnSuccess = () => Task.FromResult(0);
            return t;
        }

        static bool SplitHostPort(string s, int defPort, out string host, out int port)
        {
            host = s; port = defPort;
            if (s.StartsWith("["))
            {
                int close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                if (close + 2 < s.Length && s[close + 1] == ':') return int.TryParse(s.Substring(close + 2), out port);
                return true;
            }
            int i = s.LastIndexOf(':');
            if (i > 0 && s.IndexOf(':') == i)
            {
                host = s.Substring(0, i);
                return int.TryParse(s.Substring(i + 1), out port);
            }
            return host.Length > 0;
        }

        static int FindHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        static async Task<bool> ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = await s.ReadAsync(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        static async Task<string> ReadZString(Stream s)
        {
            var sb = new StringBuilder();
            var b = new byte[1];
            for (int i = 0; i < 512; i++)
            {
                if (!await ReadExact(s, b, 1)) return null;
                if (b[0] == 0) return sb.ToString();
                sb.Append((char)b[0]);
            }
            return null;
        }

        static Task Write(Stream s, byte[] data) { return s.WriteAsync(data, 0, data.Length); }
    }

    // ------------------------------------------------------------------ Descoberta e health check
    public static class Net
    {
        static readonly IPEndPoint[] Probes =
        {
            new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443),
            new IPEndPoint(IPAddress.Parse("9.9.9.9"), 443),
        };

        public static List<Link> Discover()
        {
            var result = new List<Link>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (ni.Name == Capture.AdapterName) continue; // adaptador virtual da captura: não é um link
                IPInterfaceProperties p;
                try { p = ni.GetIPProperties(); } catch { continue; }
                var gw = p.GatewayAddresses.Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw == null) continue;
                var ip = p.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) continue;
                var ip6 = p.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal)
                    .OrderBy(u => u.SuffixOrigin == SuffixOrigin.Random ? 1 : 0).Select(u => u.Address).FirstOrDefault();
                var t = ni.NetworkInterfaceType;
                result.Add(new Link
                {
                    Id = ni.Id, Name = ni.Name, Description = ni.Description, Address = ip, Address6 = ip6, Gateway = gw.ToString(),
                    SpeedBps = ni.Speed,
                    Wired = t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet ||
                            t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx ||
                            t == NetworkInterfaceType.Ethernet3Megabit
                });
            }
            return result;
        }

        // Baixa de speed.cloudflare.com com 4 conexões vinculadas ao IP do link e devolve a taxa em Mbps.
        public static async Task<double> MeasureMbps(Link l, int seconds)
        {
            const string host = "speed.cloudflare.com";
            long bytes = 0;
            var sockets = new ConcurrentBag<Socket>();
            IPAddress ip;
            try { ip = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch { return 0; }
            var sw = Stopwatch.StartNew();
            var limit = TimeSpan.FromSeconds(seconds);
            var tasks = Enumerable.Range(0, 4).Select(async i =>
            {
                var buf = new byte[65536];
                // o servidor limita cada resposta (< 100 MB), então repete pedidos de 50 MB até o tempo acabar
                for (int round = 0; round < 50 && sw.Elapsed < limit; round++)
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    sockets.Add(s);
                    try
                    {
                        s.Bind(new IPEndPoint(l.Address, 0));
                        await Task.Factory.FromAsync<EndPoint>(s.BeginConnect, s.EndConnect, new IPEndPoint(ip, 443), null);
                        var ssl = new System.Net.Security.SslStream(new NetworkStream(s, true));
                        await ssl.AuthenticateAsClientAsync(host, null, System.Security.Authentication.SslProtocols.Tls12, false);
                        var req = Encoding.ASCII.GetBytes("GET /__down?bytes=50000000 HTTP/1.1\r\nHost: " + host +
                                                          "\r\nUser-Agent: MultiLinkBalancer\r\nConnection: close\r\n\r\n");
                        await ssl.WriteAsync(req, 0, req.Length);
                        int n = await ssl.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0 || !Encoding.ASCII.GetString(buf, 0, Math.Min(n, 12)).EndsWith(" 200")) return;
                        Interlocked.Add(ref bytes, n);
                        while (sw.Elapsed < limit && (n = await ssl.ReadAsync(buf, 0, buf.Length)) > 0) Interlocked.Add(ref bytes, n);
                    }
                    catch { return; }
                    finally { try { s.Close(); } catch { } }
                }
            }).ToArray();
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(limit + TimeSpan.FromSeconds(5)));
            double elapsed = Math.Min(sw.Elapsed.TotalSeconds, limit.TotalSeconds + 1);
            foreach (var s in sockets) { try { s.Close(); } catch { } }
            return Interlocked.Read(ref bytes) * 8 / elapsed / 1e6;
        }

        public static async Task Check(Link l, Action<string> log)
        {
            if (!l.Present) return;
            bool lost = false; // um teste anterior desta rodada não respondeu
            foreach (var ep in Probes)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var sw = Stopwatch.StartNew();
                    s.Bind(new IPEndPoint(l.Address, 0));
                    var ct = Task.Factory.FromAsync<EndPoint>(s.BeginConnect, s.EndConnect, ep, null);
                    if (await Task.WhenAny(ct, Task.Delay(3000)) != ct)
                    {
                        ct.ContinueWith(x => { var ignored = x.Exception; });
                        lost = true;
                        continue;
                    }
                    await ct;
                    l.LatencyMs = (int)sw.ElapsedMilliseconds;
                    l.LatencyAvg = l.LatencyAvg < 0 ? l.LatencyMs : l.LatencyAvg * 0.7 + l.LatencyMs * 0.3;
                    l.ConsecutiveFailures = 0;
                    l.GatewayReachable = true;
                    l.Up = true;
                    await SampleQuality(l, lost || l.LatencyMs >= 900 ? -1 : l.LatencyMs);
                    return;
                }
                catch { lost = true; }
                finally { try { s.Close(); } catch { } }
            }
            // internet não respondeu: o modem/roteador (gateway) responde? Isso separa
            // "problema na operadora" de "modem desligado/travado"
            l.GatewayReachable = await PingGateway(l.Gateway);
            l.ConsecutiveFailures++;
            if (l.ConsecutiveFailures >= 2 && l.Up)
            {
                l.Up = false;
                l.LatencyMs = -1;
                l.LatencyAvg = -1;
                l.ResetPings(); // ao voltar, a qualidade é medida do zero
            }
        }

        static readonly IPAddress PingTarget = IPAddress.Parse("1.1.1.1");

        // tcpMs: tempo da conexão de teste, ou -1 se ela se perdeu (o Windows reenvia o SYN após ~1 s)
        static async Task SampleQuality(Link l, int tcpMs)
        {
            int icmp = -1;
            if (!l.IcmpBlocked)
            {
                var src = l.Address;
                icmp = await Task.Run(() => Icmp.Echo(src, PingTarget, 1000));
                if (icmp >= 0 && !l.IcmpOk) { l.ResetPings(); l.IcmpOk = true; } // troca para ICMP: recomeça a janela
                else if (!l.IcmpOk && ++l.IcmpMisses >= 5) l.IcmpBlocked = true;
            }
            l.AddPing(l.IcmpOk ? icmp : tcpMs);
        }

        static async Task<bool> PingGateway(string gw)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(gw, out ip)) return false;
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    using (var p = new Ping())
                        if ((await p.SendPingAsync(ip, 1000)).Status == IPStatus.Success) return true;
                }
                catch { }
            }
            return false;
        }
    }

    // Ping saindo por um link específico. O Ping do .NET não escolhe o endereço de origem;
    // o IcmpSendEcho2Ex escolhe, então cada link é medido pelo seu próprio caminho.
    public static class Icmp
    {
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern IntPtr IcmpCreateFile();
        [DllImport("iphlpapi.dll")] static extern bool IcmpCloseHandle(IntPtr h);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint IcmpSendEcho2Ex(IntPtr h, IntPtr evt, IntPtr apc, IntPtr ctx, uint src, uint dst,
            byte[] data, ushort size, IntPtr options, byte[] reply, uint replySize, uint timeout);

        static readonly byte[] Payload = Encoding.ASCII.GetBytes("MultiLinkBalancer-qualidade-1234");

        // Tempo de resposta em ms, ou -1 se não respondeu.
        public static int Echo(IPAddress from, IPAddress to, int timeoutMs)
        {
            var h = IcmpCreateFile();
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;
            try
            {
                var reply = new byte[128 + Payload.Length];
                uint n = IcmpSendEcho2Ex(h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    BitConverter.ToUInt32(from.GetAddressBytes(), 0), BitConverter.ToUInt32(to.GetAddressBytes(), 0),
                    Payload, (ushort)Payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)timeoutMs);
                // ICMP_ECHO_REPLY: Address (4), Status (4), RoundTripTime (4)...
                if (n == 0 || BitConverter.ToUInt32(reply, 4) != 0) return -1;
                return (int)BitConverter.ToUInt32(reply, 8);
            }
            catch { return -1; }
            finally { IcmpCloseHandle(h); }
        }
    }

    // ------------------------------------------------------------------ Operações no Windows (adaptadores e rotas)
    public static class WinNet
    {
        public static bool IsAdmin
        {
            get
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        static string Q(string s) { return "'" + s.Replace("'", "''") + "'"; }

        // Executa um script PowerShell. Se o app não for administrador e allowUac=true, pede elevação (UAC).
        public static Task<Tuple<bool, string>> RunPs(string script, bool allowUac)
        {
            return Task.Run(() =>
            {
                var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc);
                if (IsAdmin)
                {
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    using (var p = Process.Start(psi))
                    {
                        var output = p.StandardOutput.ReadToEnd();
                        var err = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        return Tuple.Create(p.ExitCode == 0, (p.ExitCode == 0 ? output : err).Trim());
                    }
                }
                if (!allowUac) return Tuple.Create(false, "requer executar como administrador");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        return Tuple.Create(p.ExitCode == 0, p.ExitCode == 0 ? "" : "falhou (código " + p.ExitCode + ")");
                    }
                }
                catch (System.ComponentModel.Win32Exception) { return Tuple.Create(false, "cancelado no aviso do Windows (UAC)"); }
            });
        }

        public static Task<Tuple<bool, string>> SetAdapterEnabled(string name, bool enable)
        {
            return RunPs((enable ? "Enable" : "Disable") + "-NetAdapter -Name " + Q(name) + " -Confirm:$false", true);
        }

        // Tira a interface da preferência de rota do Windows (métrica alta). Devolve "AutomaticMetric;Metric" original.
        public static Task<Tuple<bool, string>> RaiseMetric(string alias)
        {
            return RunPs("$i = Get-NetIPInterface -InterfaceAlias " + Q(alias) + " -AddressFamily IPv4; " +
                         "Write-Output ('{0};{1}' -f $i.AutomaticMetric, $i.InterfaceMetric); " +
                         "Get-NetIPInterface -InterfaceAlias " + Q(alias) + " | Set-NetIPInterface -InterfaceMetric 9000", false);
        }

        public static Task<Tuple<bool, string>> RestoreMetric(string alias, string saved)
        {
            var p = (saved ?? "Enabled;0").Split(';');
            var set = p[0] == "Enabled" || p.Length < 2 ? "-AutomaticMetric Enabled" : "-InterfaceMetric " + p[1];
            return RunPs("Get-NetIPInterface -InterfaceAlias " + Q(alias) + " | Set-NetIPInterface " + set, false);
        }

        public static bool TaskExists(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN " + name) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        // Tarefa agendada no logon com privilégio máximo (o atalho "Run" do registro não eleva).
        public static Task<Tuple<bool, string>> RegisterElevatedStartup(string name, string exe)
        {
            return RunPs("$a = New-ScheduledTaskAction -Execute " + Q(exe) + " -Argument '/tray'; " +
                         "$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; " +
                         "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; " +
                         "$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive; " +
                         "Register-ScheduledTask -TaskName " + Q(name) + " -Action $a -Trigger $t -Settings $s -Principal $p -Force | Out-Null", false);
        }

        public static Task<Tuple<bool, string>> UnregisterStartupTask(string name)
        {
            return RunPs("Unregister-ScheduledTask -TaskName " + Q(name) + " -Confirm:$false -ErrorAction SilentlyContinue", false);
        }
    }

    // ------------------------------------------------------------------ Monitor de quedas e failover
    // Lógica independente de interface: avalia o estado de cada link, dispara HealthChanged nas
    // transições e, se ativado, tira do roteamento do Windows o link que caiu (e devolve quando volta).
    public class LinkMonitor
    {
        public const string Ok = "ok", NoInternet = "noinet", GatewayDown = "nogw", Unplugged = "unplugged", Disabled = "disabled", NoConfig = "nocfg";

        readonly Config cfg;
        readonly Action<string> log;
        public bool AutoRoute;
        public event Action<Link, string, string> HealthChanged; // link, anterior, atual

        public LinkMonitor(Config cfg, Action<string> log) { this.cfg = cfg; this.log = log; }

        public static string HealthOf(Link l)
        {
            if (!l.Present) return l.AdapterState ?? Unplugged;
            if (!l.Up) return l.GatewayReachable ? NoInternet : GatewayDown;
            return Ok;
        }

        public static string ShortText(string h)
        {
            switch (h)
            {
                case Ok: return "● Online";
                case NoInternet: return "● Sem internet (operadora)";
                case GatewayDown: return "● Modem não responde";
                case Unplugged: return "● Cabo desconectado";
                case Disabled: return "○ Desativado no Windows";
                case NoConfig: return "● Sem IP/gateway";
                default: return h;
            }
        }

        public static string Describe(string h)
        {
            switch (h)
            {
                case NoInternet: return "sem internet. O modem responde, então o problema é na operadora";
                case GatewayDown: return "o modem/roteador não responde (desligado, travado ou cabo com defeito)";
                case Unplugged: return "cabo desconectado";
                case Disabled: return "adaptador desativado no Windows";
                case NoConfig: return "sem IP ou gateway (o modem não entregou endereço)";
                default: return "online";
            }
        }

        // Chamado periodicamente (1 s) com a lista atual de links.
        public void Evaluate(List<Link> links)
        {
            foreach (var l in links)
            {
                var h = HealthOf(l);
                var prev = l.Health;
                l.Health = h;
                if (prev == null) { if (h != Ok) l.DownSince = DateTime.Now; continue; }
                if (prev == h) continue;
                if (prev == Ok) l.DownSince = DateTime.Now;
                if (h == Ok && l.UserDisabled) l.UserDisabled = false;
                var handler = HealthChanged;
                if (handler != null) handler(l, prev, h);
            }
            if (AutoRoute && WinNet.IsAdmin) ApplyRouting(links);
        }

        // Link fora (mas com cabo) + outro link saudável => métrica alta. Link de volta => métrica original.
        void ApplyRouting(List<Link> links)
        {
            bool anyHealthy = links.Any(x => x.Enabled && x.Health == Ok);
            foreach (var l in links)
            {
                if (l.MetricBusy || !l.Enabled) continue;
                bool shouldRaise = l.Present && l.Health != Ok && anyHealthy;
                if (shouldRaise && !l.MetricRaised) Raise(l);
                else if (!shouldRaise && l.MetricRaised && l.Health == Ok) Restore(l);
            }
        }

        async void Raise(Link l)
        {
            l.MetricBusy = true;
            try
            {
                var r = await WinNet.RaiseMetric(l.Name);
                if (r.Item1)
                {
                    if (!cfg.Has("metric." + l.Id)) { cfg.Set("metric." + l.Id, l.Name + "|" + r.Item2); cfg.Save(); }
                    l.MetricRaised = true;
                    log("[" + l.Name + "] retirado do roteamento do Windows: o sistema inteiro passa a usar os outros links");
                }
                else log("[" + l.Name + "] não foi possível retirar do roteamento: " + r.Item2);
            }
            finally { l.MetricBusy = false; }
        }

        async void Restore(Link l)
        {
            l.MetricBusy = true;
            try
            {
                var saved = cfg.Get("metric." + l.Id, null);
                var r = await WinNet.RestoreMetric(l.Name, saved == null ? null : saved.Substring(saved.IndexOf('|') + 1));
                if (r.Item1)
                {
                    cfg.Remove("metric." + l.Id); cfg.Save();
                    l.MetricRaised = false;
                    log("[" + l.Name + "] devolvido ao roteamento do Windows");
                }
                else log("[" + l.Name + "] não foi possível restaurar a rota: " + r.Item2);
            }
            finally { l.MetricBusy = false; }
        }

        // Ao sair (ou ao abrir após um fechamento inesperado): devolve todas as métricas alteradas.
        public void RestoreAll()
        {
            foreach (var key in cfg.Keys().Where(k => k.StartsWith("metric.")).ToList())
            {
                var v = cfg.Get(key, "");
                int bar = v.IndexOf('|');
                if (bar <= 0) { cfg.Remove(key); continue; }
                var r = WinNet.RestoreMetric(v.Substring(0, bar), v.Substring(bar + 1));
                if (r.Wait(15000) && r.Result.Item1) cfg.Remove(key);
            }
            cfg.Save();
        }
    }

    // ------------------------------------------------------------------ Download acelerado
    // Divide um arquivo em pedaços (HTTP Range) e baixa por todos os links ao mesmo tempo.
    // Cada link recebe vários "workers" que pegam o próximo pedaço livre: o link mais rápido
    // naturalmente baixa mais pedaços, então as bandas se somam.
    public class SegmentedDownload
    {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MultiLinkBalancer/1.0";
        const int WorkersPerLink = 4;
        public static bool Verbose;
        readonly Action<string> log;
        public long Total = -1;
        public long Done;
        public readonly ConcurrentDictionary<Link, long> PerLink = new ConcurrentDictionary<Link, long>();
        int inFlight;

        class Worker { public Socket Sock; public long LastActivity; }
        readonly ConcurrentBag<Worker> workers = new ConcurrentBag<Worker>();

        public SegmentedDownload(Action<string> log) { this.log = log; }

        // Retorna URL final (após redirecionamentos), tamanho e se o servidor aceita Range.
        public static async Task<Tuple<Uri, long, bool>> Probe(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            req.AllowAutoRedirect = true;
            req.Timeout = 20000;
            req.AddRange(0L, 0L);
            using (var resp = (HttpWebResponse)await req.GetResponseAsync())
            {
                long total = -1;
                bool ranges = false;
                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    var cr = resp.Headers["Content-Range"] ?? "";
                    int slash = cr.LastIndexOf('/');
                    ranges = slash > 0 && long.TryParse(cr.Substring(slash + 1), out total) && total > 0;
                }
                else total = resp.ContentLength;
                return Tuple.Create(resp.ResponseUri, total, ranges);
            }
        }

        public async Task Run(Uri uri, long total, string path, List<Link> links, CancellationToken ct)
        {
            Total = total;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(total);
            long chunk = Math.Max(1L << 20, Math.Min(8L << 20, total / (links.Count * WorkersPerLink * 4)));
            var queue = new ConcurrentQueue<long>();
            for (long o = 0; o < total; o += chunk) queue.Enqueue(o);

            var ip = (await Dns.GetHostAddressesAsync(uri.DnsSafeHost)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null) throw new IOException("o servidor não tem endereço IPv4");

            var tasks = new List<Task>();
            foreach (var l in links)
                for (int i = 0; i < WorkersPerLink; i++)
                    tasks.Add(RunWorker(l, uri, ip, queue, chunk, total, path, ct));

            // watchdog: fecha conexões paradas há mais de 20 s (o pedaço volta para a fila)
            var all = Task.WhenAll(tasks);
            while (await Task.WhenAny(all, Task.Delay(2000)) != all)
            {
                long now = Stopwatch.GetTimestamp();
                foreach (var w in workers)
                {
                    var s = w.Sock;
                    if (s != null && (now - Interlocked.Read(ref w.LastActivity)) > Stopwatch.Frequency * 20) { try { s.Close(); } catch { } }
                    if (ct.IsCancellationRequested && s != null) { try { s.Close(); } catch { } }
                }
            }
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref Done) < total) throw new IOException("download incompleto: todos os links falharam");
        }

        async Task RunWorker(Link l, Uri uri, IPAddress ip, ConcurrentQueue<long> queue, long chunk, long total, string path, CancellationToken ct)
        {
            var w = new Worker();
            workers.Add(w);
            Stream st = null;
            int fails = 0, wait = 0;
            bool retired = false;
            lock (linkWorkers) { int cnt; linkWorkers.TryGetValue(l, out cnt); linkWorkers[l] = cnt + 1; }
            var buf = new byte[65536];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                while (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref inFlight);
                    long off;
                    if (!queue.TryDequeue(out off))
                    {
                        // fila vazia: espera caso algum pedaço em andamento falhe e volte para a fila
                        if (Interlocked.Decrement(ref inFlight) == 0 && queue.IsEmpty) break;
                        await Task.Delay(200);
                        continue;
                    }
                    long end = Math.Min(off + chunk, total) - 1, got = 0;
                    try
                    {
                        if (st == null)
                        {
                            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            w.Sock = s;
                            Interlocked.Exchange(ref w.LastActivity, Stopwatch.GetTimestamp());
                            s.Bind(new IPEndPoint(l.Address, 0));
                            s.ReceiveBufferSize = 1 << 20;
                            var conn = Task.Factory.FromAsync<EndPoint>(s.BeginConnect, s.EndConnect, new IPEndPoint(ip, uri.Port), null);
                            if (await Task.WhenAny(conn, Task.Delay(8000)) != conn)
                            {
                                conn.ContinueWith(x => { var ignored = x.Exception; });
                                s.Close();
                                throw new TimeoutException("tempo de conexão esgotado");
                            }
                            await conn;
                            st = new NetworkStream(s, true);
                            if (uri.Scheme == Uri.UriSchemeHttps)
                            {
                                var ssl = new System.Net.Security.SslStream(st, false);
                                await ssl.AuthenticateAsClientAsync(uri.DnsSafeHost, null, System.Security.Authentication.SslProtocols.Tls12, false);
                                st = ssl;
                            }
                        }
                        var req = Encoding.ASCII.GetBytes("GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Authority +
                            "\r\nUser-Agent: " + UA + "\r\nAccept: */*\r\nAccept-Encoding: identity\r\nRange: bytes=" + off + "-" + end +
                            "\r\nConnection: keep-alive\r\n\r\n");
                        await st.WriteAsync(req, 0, req.Length);

                        var header = await ReadHeader(st);
                        var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var status = lines[0].Split(' ');
                        long len = -1; bool close = false; int retryAfter = 0;
                        foreach (var h in lines.Skip(1))
                        {
                            int c = h.IndexOf(':'); if (c <= 0) continue;
                            var name = h.Substring(0, c).Trim().ToLowerInvariant(); var val = h.Substring(c + 1).Trim();
                            if (name == "content-length") long.TryParse(val, out len);
                            else if (name == "connection" && val.Equals("close", StringComparison.OrdinalIgnoreCase)) close = true;
                            else if (name == "retry-after") int.TryParse(val, out retryAfter);
                        }
                        if (status.Length >= 2 && (status[1] == "429" || status[1] == "503"))
                            throw new ThrottledException(lines[0], retryAfter);
                        if (status.Length < 2 || status[1] != "206") throw new IOException("resposta inesperada: " + lines[0]);
                        if (len != end - off + 1) throw new IOException("tamanho do pedaço inesperado");

                        fs.Seek(off, SeekOrigin.Begin);
                        while (got < len)
                        {
                            int n = await st.ReadAsync(buf, 0, (int)Math.Min(buf.Length, len - got));
                            if (n <= 0) throw new IOException("conexão encerrada");
                            await fs.WriteAsync(buf, 0, n);
                            got += n;
                            Interlocked.Add(ref Done, n);
                            Interlocked.Add(ref l.BytesDown, n);
                            PerLink.AddOrUpdate(l, n, (k, v) => v + n);
                            Interlocked.Exchange(ref w.LastActivity, Stopwatch.GetTimestamp());
                        }
                        if (close) { st.Dispose(); st = null; }
                        fails = 0;
                        if (Verbose) log(string.Format("[{0}] pedaço {1} ok ({2} MB){3}", l.Name, off / chunk, len >> 20, close ? " close" : ""));
                    }
                    catch (Exception ex)
                    {
                        if (Verbose) log(string.Format("[{0}] pedaço {1} falhou após {2} KB: {3}", l.Name, off / chunk, got >> 10, ex.Message));
                        Interlocked.Add(ref Done, -got);
                        queue.Enqueue(off);
                        if (st != null) { try { st.Dispose(); } catch { } st = null; }
                        var th = ex as ThrottledException;
                        if (th != null)
                        {
                            // servidor limitando conexões por IP: reduz as conexões deste link, mas mantém ao menos uma
                            if ((retired = TryRetire(l)))
                            {
                                log(string.Format("[{0}] servidor pediu menos conexões ({1}); usando {2} neste link", l.Name, th.Message, LinkWorkers(l)));
                                Interlocked.Decrement(ref inFlight);
                                break;
                            }
                            wait = Math.Max(1000, Math.Min(15000, th.RetryAfterSec * 1000));
                        }
                        else if (!ct.IsCancellationRequested && ++fails >= 3 && ((retired = TryRetire(l)) || fails >= 8))
                        {
                            log(string.Format("[{0}] download: conexão desistiu após 3 falhas ({1})", l.Name, ex.Message));
                            Interlocked.Decrement(ref inFlight);
                            break;
                        }
                        else wait = 1000 * Math.Min(fails, 10);
                    }
                    Interlocked.Decrement(ref inFlight);
                    if (wait > 0) { await Task.Delay(wait); wait = 0; }
                }
            }
            if (st != null) { try { st.Dispose(); } catch { } }
            w.Sock = null;
            lock (linkWorkers) { int n; if (linkWorkers.TryGetValue(l, out n) && n > 0 && !retired) linkWorkers[l] = n - 1; }
        }

        readonly Dictionary<Link, int> linkWorkers = new Dictionary<Link, int>();

        int LinkWorkers(Link l) { lock (linkWorkers) { int n; linkWorkers.TryGetValue(l, out n); return n; } }

        bool TryRetire(Link l)
        {
            lock (linkWorkers)
            {
                int n; linkWorkers.TryGetValue(l, out n);
                if (n <= 1) return false;
                linkWorkers[l] = n - 1;
                return true;
            }
        }

        class ThrottledException : IOException
        {
            public readonly int RetryAfterSec;
            public ThrottledException(string msg, int retryAfterSec) : base(msg) { RetryAfterSec = retryAfterSec; }
        }

        static async Task<string> ReadHeader(Stream s)
        {
            var sb = new StringBuilder();
            var b = new byte[1];
            while (sb.Length < 32768)
            {
                if (await s.ReadAsync(b, 0, 1) <= 0) throw new IOException("conexão encerrada");
                sb.Append((char)b[0]);
                int n = sb.Length;
                if (n >= 4 && sb[n - 1] == '\n' && sb[n - 2] == '\r' && sb[n - 3] == '\n' && sb[n - 4] == '\r') return sb.ToString(0, n - 4);
            }
            throw new IOException("cabeçalho muito grande");
        }

        // Servidor sem suporte a Range: baixa normalmente por uma conexão só.
        public async Task RunSingle(Uri uri, string path, CancellationToken ct)
        {
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.UserAgent = UA;
            using (var resp = (HttpWebResponse)await req.GetResponseAsync())
            using (var rs = resp.GetResponseStream())
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                Total = resp.ContentLength;
                var buf = new byte[65536];
                int n;
                while ((n = await rs.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    await fs.WriteAsync(buf, 0, n);
                    Interlocked.Add(ref Done, n);
                }
            }
        }
    }

    // ------------------------------------------------------------------ Configuração
    public class Config
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiLinkBalancer");
        static readonly string FilePath = Path.Combine(Dir, "config.ini");
        readonly Dictionary<string, string> v = new Dictionary<string, string>();

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) c.v[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, v.Select(p => p.Key + "=" + p.Value), Encoding.UTF8);
            }
            catch { }
        }

        public string Get(string k, string def) { string s; return v.TryGetValue(k, out s) ? s : def; }
        public int GetInt(string k, int def) { int i; return int.TryParse(Get(k, null), out i) ? i : def; }
        public bool GetBool(string k, bool def) { var s = Get(k, null); return s == null ? def : s == "1"; }
        public bool Has(string k) { return v.ContainsKey(k); }
        public void Set(string k, object val) { v[k] = val is bool ? ((bool)val ? "1" : "0") : (val == null ? "" : val.ToString()); }
        public void Remove(string k) { v.Remove(k); }
        public List<string> Keys() { return v.Keys.ToList(); }
    }

    // ------------------------------------------------------------------ Proxy do sistema / inicialização
    public static class SystemIntegration
    {
        const string InetKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "MultiLinkBalancer";

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int len);

        public static void ApplyProxy(Config cfg, int port)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(InetKey))
            {
                if (!cfg.GetBool("proxyApplied", false))
                {
                    var en = k.GetValue("ProxyEnable");
                    cfg.Set("prev.ProxyEnable", en == null ? "" : en.ToString());
                    cfg.Set("prev.ProxyServer", (k.GetValue("ProxyServer") ?? "\u0000").ToString());
                    cfg.Set("prev.ProxyOverride", (k.GetValue("ProxyOverride") ?? "\u0000").ToString());
                }
                k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", "127.0.0.1:" + port);
                k.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;172.16.*;192.168.*;*.multilink");
            }
            cfg.Set("proxyApplied", true);
            cfg.Save();
            Refresh();
        }

        public static void RestoreProxy(Config cfg)
        {
            if (!cfg.GetBool("proxyApplied", false)) return;
            using (var k = Registry.CurrentUser.CreateSubKey(InetKey))
            {
                int en;
                if (int.TryParse(cfg.Get("prev.ProxyEnable", ""), out en)) k.SetValue("ProxyEnable", en, RegistryValueKind.DWord);
                else k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                RestoreString(k, "ProxyServer", cfg.Get("prev.ProxyServer", "\u0000"));
                RestoreString(k, "ProxyOverride", cfg.Get("prev.ProxyOverride", "\u0000"));
            }
            cfg.Set("proxyApplied", false);
            cfg.Save();
            Refresh();
        }

        static void RestoreString(RegistryKey k, string name, string val)
        {
            if (val == "\u0000") k.DeleteValue(name, false);
            else k.SetValue(name, val);
        }

        static void Refresh()
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // INTERNET_OPTION_REFRESH
        }

        public static bool RunKeyStartup
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null; }
        }

        public static bool StartupEnabled { get { return RunKeyStartup || WinNet.TaskExists(AppName); } }

        // Como administrador usa uma tarefa agendada elevada (necessária para o failover automático);
        // sem admin usa a chave "Run" do registro.
        public static async Task SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue(AppName, false);
            if (WinNet.IsAdmin) await WinNet.UnregisterStartupTask(AppName);
            if (!on) return;
            if (WinNet.IsAdmin)
            {
                var r = await WinNet.RegisterElevatedStartup(AppName, Application.ExecutablePath);
                if (!r.Item1) throw new Exception(r.Item2);
            }
            else
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" /tray");
        }
    }
}
