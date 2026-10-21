using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

        // Com o "weak host send" ligado no adaptador, o bind no IP do link não escolhe a saída: o socket segue a melhor
        // rota do sistema (as rotas da captura) e volta para o próprio MultiLink. No IPv6 o Windows já vem assim; no IPv4,
        // o Hotspot Móvel, o Compartilhamento de Internet, o Hyper-V e algumas VPNs ligam. IP_UNICAST_IF força a saída.
        // O IPv4 recebe o índice na ordem de bytes da rede; o IPv6, na ordem do PC.
        const SocketOptionName UnicastInterface = (SocketOptionName)31;

        public static void UseInterfaceV4(this Socket socket, int interfaceIndex)
        {
            if (interfaceIndex > 0) socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, IPAddress.HostToNetworkOrder(interfaceIndex));
        }

        public static void UseInterfaceV6(this Socket socket, int interfaceIndex)
        {
            if (interfaceIndex > 0) socket.SetSocketOption(SocketOptionLevel.IPv6, UnicastInterface, interfaceIndex);
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

        // No .NET Framework os sockets nascem herdáveis, e todo processo aberto pelo app (PowerShell, netsh) recebe uma
        // cópia. Se um deles ainda estiver rodando quando o MultiLink fecha, segura a porta do proxy e o MultiLink aberto
        // logo depois não consegue abri-la ("Only one usage of each socket address").
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
        const int HandleFlagInherit = 1;

        public static void DisableInheritance(this Socket socket)
        {
            SetHandleInformation(socket.Handle, HandleFlagInherit, 0);
        }

        public static void CloseQuietly(this Socket socket)
        {
            if (socket == null) return;
            try { socket.Close(); } catch { }
        }
    }
}
