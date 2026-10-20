// Mede a velocidade de download de um link: baixa de speed.cloudflare.com por 4 conexões
// vinculadas ao IP do link durante alguns segundos.
using System;
using System.Collections.Concurrent;
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

        // Devolve a taxa em Mbps (0 se não conseguiu medir).
        public static async Task<double> MeasureMbps(Link link, int seconds)
        {
            IPAddress server;
            try { server = (await Dns.GetHostAddressesAsync(Host)).First(address => address.AddressFamily == AddressFamily.InterNetwork); }
            catch { return 0; }

            long received = 0;
            var sockets = new ConcurrentBag<Socket>();
            var stopwatch = Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(seconds);
            Func<bool> timeLeft = () => stopwatch.Elapsed < duration;
            Action<int> count = bytes => Interlocked.Add(ref received, bytes);

            var downloads = Enumerable.Range(0, Connections).Select(i => Download(link, server, sockets, timeLeft, count)).ToArray();
            await Task.WhenAny(Task.WhenAll(downloads), Task.Delay(duration + TimeSpan.FromSeconds(5)));
            double elapsed = Math.Min(stopwatch.Elapsed.TotalSeconds, duration.TotalSeconds + 1);
            foreach (var socket in sockets) socket.CloseQuietly();
            return Interlocked.Read(ref received) * 8 / elapsed / 1e6;
        }

        static async Task Download(Link link, IPAddress server, ConcurrentBag<Socket> sockets, Func<bool> timeLeft, Action<int> count)
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
                        long remaining = BodyLength(Encoding.ASCII.GetString(buffer, 0, headerEnd));
                        if (remaining < 0) return; // recusado (429 etc.): insistir não adianta
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

        // Tamanho do corpo de uma resposta 200 com Content-Length; -1 para qualquer outra.
        static long BodyLength(string header)
        {
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (!lines[0].Contains(" 200")) return -1;
            foreach (var line in lines)
            {
                long length;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.Substring(15).Trim(), out length)) return length;
            }
            return -1;
        }
    }
}
