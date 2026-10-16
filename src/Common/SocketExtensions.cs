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

        // No IPv6 o Windows costuma deixar o "weak host send" ligado: preso só ao IP do link, o socket seguiria a
        // melhor rota do sistema (a 2000::/3 da captura) e voltaria para o próprio MultiLink. IPV6_UNICAST_IF força a saída.
        const SocketOptionName IPv6UnicastInterface = (SocketOptionName)31;

        public static void UseInterfaceV6(this Socket socket, int interfaceIndex)
        {
            if (interfaceIndex > 0) socket.SetSocketOption(SocketOptionLevel.IPv6, IPv6UnicastInterface, interfaceIndex);
        }

        public static void CloseQuietly(this Socket socket)
        {
            if (socket == null) return;
            try { socket.Close(); } catch { }
        }
    }
}
