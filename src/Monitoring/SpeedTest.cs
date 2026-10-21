// Mede a velocidade de download de um link: baixa de speed.cloudflare.com por 4 conexões
// vinculadas ao IP do link durante alguns segundos. Também mede vários links juntos (teste da soma).
// Se o servidor recusar algum pedido (403/429 depois de muitos testes seguidos), a medição daquele link
// é marcada como recusada e as próximas ficam pausadas por alguns minutos.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiLink.Common;
using MultiLink.Links;

namespace MultiLink.Monitoring
{
    public static class SpeedTest
    {
        const string Host = "speed.cloudflare.com";
        const int Connections = 4;
        // Pedidos de 2 MB em sequência na mesma conexão (keep-alive): depois de uma rajada, o servidor responde 429
        // por vários minutos aos pedidos grandes (10 MB ou mais) daquele IP, mas continua atendendo os pequenos.
        // Assim o teste da soma e a medição dos pesos podem rodar um logo depois do outro.
        const int RequestBytes = 2000000;
        const int MaxReconnects = 10; // se o servidor fechar a conexão, abre outra
        const int MaxHeaderBytes = 8192;
        // pausa depois de um 403/429 sem Retry-After; com ele, o valor do servidor dentro destes limites
        static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(10), MinPause = TimeSpan.FromMinutes(1), MaxPause = TimeSpan.FromMinutes(30);

        static readonly object pauseLock = new object();
        static DateTime pausedUntil = DateTime.MinValue;
        static int pauseStatus;

        // Até quando as medições estão pausadas por bloqueio do servidor; null se não estão.
        public static DateTime? PausedUntil { get { lock (pauseLock) return DateTime.Now < pausedUntil ? pausedUntil : (DateTime?)null; } }

        // Lança a explicação se o servidor bloqueou há pouco: medir agora só prolongaria o bloqueio.
        public static void EnsureNotPaused()
        {
            int status;
            lock (pauseLock) status = DateTime.Now < pausedUntil ? pauseStatus : 0;
            if (status != 0) throw new Exception(Capitalize(RefusalText(status)) + ".");
        }

        // Motivo legível de uma recusa, ex.: "o servidor de medição (...) bloqueou temporariamente os pedidos
        // deste PC (HTTP 429); dá para medir de novo às 14:35".
        public static string RefusalText(int status)
        {
            if (!IsBlock(status)) return "o servidor de medição (" + Host + ") recusou os pedidos (HTTP " + status + ")";
            var until = PausedUntil;
            return "o servidor de medição (" + Host + ") bloqueou temporariamente os pedidos deste PC (HTTP " + status + ")" +
                   (until.HasValue ? "; dá para medir de novo às " + until.Value.ToString("HH:mm") : "");
        }

        public static string Capitalize(string text) { return char.ToUpper(text[0]) + text.Substring(1); }

        static bool IsBlock(int status) { return status == 403 || status == 429; }

        // Mede um link (0 Mbps se não conseguiu medir).
        public static async Task<SpeedResult> Measure(Link link, int seconds)
        {
            return (await MeasureTogether(new[] { link }, seconds))[0];
        }

        // Mede vários links ao mesmo tempo, com o mesmo cronômetro: a soma das taxas é o que os links
        // rendem juntos. Devolve a medição de cada link, na ordem recebida.
        public static async Task<SpeedResult[]> MeasureTogether(IList<Link> links, int seconds)
        {
            EnsureNotPaused();
            var received = new long[links.Count];
            var refused = new int[links.Count];
            IPAddress server;
            try { server = (await Dns.GetHostAddressesAsync(Host)).First(address => address.AddressFamily == AddressFamily.InterNetwork); }
            catch { return links.Select(link => new SpeedResult()).ToArray(); }

            var sockets = new ConcurrentBag<Socket>();
            var stopwatch = Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(seconds);
            Func<bool> timeLeft = () => stopwatch.Elapsed < duration;

            var downloads = links.SelectMany((link, index) => Enumerable.Range(0, Connections).Select(i =>
                Download(link, server, sockets, timeLeft, bytes => Interlocked.Add(ref received[index], bytes),
                         status => Interlocked.CompareExchange(ref refused[index], status, 0)))).ToArray();
            await Task.WhenAny(Task.WhenAll(downloads), Task.Delay(duration + TimeSpan.FromSeconds(5)));
            double elapsed = Math.Min(stopwatch.Elapsed.TotalSeconds, duration.TotalSeconds + 1);
            foreach (var socket in sockets) socket.CloseQuietly();
            return Enumerable.Range(0, links.Count).Select(index => new SpeedResult
            {
                Mbps = Interlocked.Read(ref received[index]) * 8 / elapsed / 1e6,
                RefusedStatus = Volatile.Read(ref refused[index])
            }).ToArray();
        }

        // Um 403/429 pausa as medições pelo Retry-After do servidor (ou pelo padrão).
        static void Pause(int status, TimeSpan? retryAfter)
        {
            var pause = retryAfter ?? DefaultPause;
            if (pause < MinPause) pause = MinPause;
            if (pause > MaxPause) pause = MaxPause;
            lock (pauseLock)
            {
                var until = DateTime.Now + pause;
                if (until > pausedUntil) { pausedUntil = until; pauseStatus = status; }
            }
        }

        static async Task Download(Link link, IPAddress server, ConcurrentBag<Socket> sockets, Func<bool> timeLeft, Action<int> count, Action<int> refuse)
        {
            var buffer = new byte[65536];
            var request = Encoding.ASCII.GetBytes("GET /__down?bytes=" + RequestBytes + " HTTP/1.1\r\nHost: " + Host + "\r\nUser-Agent: MultiLinkBalancer\r\n\r\n");
            for (int attempt = 0; attempt < MaxReconnects && timeLeft(); attempt++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(socket);
                try
                {
                    socket.BindForConnect(link.Address);
                    socket.UseInterfaceV4(link.InterfaceIndex);
                    await socket.ConnectTaskAsync(new IPEndPoint(server, 443));
                    var tls = new SslStream(new NetworkStream(socket, true));
                    await tls.AuthenticateAsClientAsync(Host, null, SslProtocols.Tls12, false);
                    while (timeLeft())
                    {
                        await tls.WriteAllAsync(request);
                        // cabeçalho lido em blocos (byte a byte custa caro a 700 Mbps); o resto do bloco já é corpo
                        int filled = 0, headerEnd;
                        while ((headerEnd = BlankLine(buffer, filled)) < 0)
                        {
                            int received = filled < MaxHeaderBytes ? await tls.ReadAsync(buffer, filled, buffer.Length - filled) : 0;
                            if (received <= 0) return;
                            filled += received;
                        }
                        var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                        int status = StatusCode(header);
                        if (status != 200)
                        {
                            // recusado: insistir não adianta, e o que as outras conexões mediram fica incompleto
                            refuse(status);
                            if (IsBlock(status)) Pause(status, RetryAfter(header));
                            return;
                        }
                        long remaining = BodyLength(header);
                        if (remaining < 0) return;
                        count(filled - headerEnd - 4);
                        remaining -= filled - headerEnd - 4;
                        int read;
                        while (remaining > 0 && timeLeft() && (read = await tls.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                        {
                            count(read);
                            remaining -= read;
                        }
                        if (remaining > 0) break; // o tempo acabou ou o servidor fechou a conexão
                    }
                }
                catch { }
                finally { socket.CloseQuietly(); }
            }
        }

        // Posição do "\r\n\r\n" que fecha o cabeçalho; -1 se ainda não chegou.
        static int BlankLine(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
            return -1;
        }

        // Código da primeira linha ("HTTP/1.1 429 Too Many Requests" -> 429); 0 se não der para ler.
        static int StatusCode(string header)
        {
            var parts = header.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            int status;
            return parts.Length > 1 && int.TryParse(parts[1], out status) ? status : 0;
        }

        // Valor de um campo do cabeçalho; null se não veio.
        static string Field(string header, string name)
        {
            foreach (var line in header.Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) return line.Substring(name.Length + 1).Trim();
            return null;
        }

        // Tamanho do corpo pelo Content-Length; -1 se não veio.
        static long BodyLength(string header)
        {
            long length;
            return long.TryParse(Field(header, "Content-Length"), out length) ? length : -1;
        }

        // Retry-After em segundos (a forma com data fica com a pausa padrão); null se não veio.
        static TimeSpan? RetryAfter(string header)
        {
            int seconds;
            return int.TryParse(Field(header, "Retry-After"), out seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        }
    }

    public class SpeedResult
    {
        public double Mbps;
        public int RefusedStatus; // código HTTP da primeira recusa do servidor (403, 429...); 0 se não houve

        public bool Refused { get { return RefusedStatus != 0; } }
    }
}
