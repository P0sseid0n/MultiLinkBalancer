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
        const int RequestBytes = 50000000; // o servidor limita cada resposta (< 100 MB): pedidos de 50 MB repetidos até o tempo acabar
        const int MaxRequestsPerConnection = 50;

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
            for (int request = 0; request < MaxRequestsPerConnection && timeLeft(); request++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(socket);
                try
                {
                    socket.BindForConnect(link.Address);
                    await socket.ConnectTaskAsync(new IPEndPoint(server, 443));
                    var tls = new SslStream(new NetworkStream(socket, true));
                    await tls.AuthenticateAsClientAsync(Host, null, SslProtocols.Tls12, false);
                    await tls.WriteAllAsync(Encoding.ASCII.GetBytes("GET /__down?bytes=" + RequestBytes + " HTTP/1.1\r\nHost: " + Host +
                                                                    "\r\nUser-Agent: MultiLinkBalancer\r\nConnection: close\r\n\r\n"));
                    int read = await tls.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0 || !Encoding.ASCII.GetString(buffer, 0, Math.Min(read, 12)).EndsWith(" 200")) return;
                    count(read);
                    while (timeLeft() && (read = await tls.ReadAsync(buffer, 0, buffer.Length)) > 0) count(read);
                }
                catch { return; }
                finally { socket.CloseQuietly(); }
            }
        }
    }
}
