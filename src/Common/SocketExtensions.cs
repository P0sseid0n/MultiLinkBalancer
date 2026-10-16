using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MultiLink.Common
{
    public static class SocketExtensions
    {
        public static Task ConnectTaskAsync(this Socket socket, EndPoint remote)
        {
            return Task.Factory.FromAsync<EndPoint>(socket.BeginConnect, socket.EndConnect, remote, null);
        }

        // Conecta ou lança TimeoutException. O socket continua aberto: fechar é com quem chamou.
        public static async Task ConnectWithTimeout(this Socket socket, EndPoint remote, int timeoutMs)
        {
            var connect = socket.ConnectTaskAsync(remote);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
            {
                connect.ContinueWith(task => { var ignored = task.Exception; }); // observa a falha que vier depois
                throw new TimeoutException("tempo de conexão esgotado");
            }
            await connect;
        }

        public static void CloseQuietly(this Socket socket)
        {
            if (socket == null) return;
            try { socket.Close(); } catch { }
        }
    }
}
