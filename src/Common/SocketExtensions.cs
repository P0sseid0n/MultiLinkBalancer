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

        // Com bind(ip, 0) antes do connect, o Windows reserva uma porta só para o socket e, se as portas efêmeras
        // acabarem (outro programa abrindo conexões demais), a conexão falha. SO_REUSE_UNICASTPORT adia a escolha
        // da porta para o connect, que pode reaproveitá-la para outro destino, como faz um connect sem bind.
        const SocketOptionName ReuseUnicastPort = (SocketOptionName)0x3007;

        public static void BindForConnect(this Socket socket, IPAddress localAddress)
        {
            try { socket.SetSocketOption(SocketOptionLevel.Socket, ReuseUnicastPort, true); } catch { }
            socket.Bind(new IPEndPoint(localAddress, 0));
        }

        // No IPv6 o Windows costuma deixar o "weak host send" ligado: preso só ao IP do link, o socket seguiria a
        // melhor rota do sistema (a 2000::/3 da captura) e voltaria para o próprio MultiLink. IPV6_UNICAST_IF força a saída.
        const SocketOptionName IPv6UnicastInterface = (SocketOptionName)31;

        public static void UseInterfaceV6(this Socket socket, int interfaceIndex)
        {
            if (interfaceIndex > 0) socket.SetSocketOption(SocketOptionLevel.IPv6, IPv6UnicastInterface, interfaceIndex);
        }

        // Keepalive do TCP: sem ele, uma conexão cujo link caiu fica esperando dados para sempre. O Windows
        // manda 10 sondas depois do tempo ocioso; sem resposta, a leitura falha e o relé fecha os dois lados.
        public static void EnableKeepAlive(this Socket socket, int idleMs, int intervalMs)
        {
            var values = new byte[12];
            BitConverter.GetBytes(1).CopyTo(values, 0);
            BitConverter.GetBytes(idleMs).CopyTo(values, 4);
            BitConverter.GetBytes(intervalMs).CopyTo(values, 8);
            try { socket.IOControl(IOControlCode.KeepAliveValues, values, null); } catch { }
        }

        // Sem isso, um ICMP "porta inacessível" faz a próxima leitura do socket UDP falhar.
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        public static void IgnoreUdpConnectionReset(this Socket socket)
        {
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
        }

        public static void CloseQuietly(this Socket socket)
        {
            if (socket == null) return;
            try { socket.Close(); } catch { }
        }
    }
}
